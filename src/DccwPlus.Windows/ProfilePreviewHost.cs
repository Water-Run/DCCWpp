using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;
using DccwPlus.Core.Storage;

namespace DccwPlus.Windows;

public sealed record ProfilePreviewRequest(Guid Id, string DisplayId, string CandidateProfile, int Seconds,
    int? OwnerProcessId = null, long? OwnerStartTicks = null, bool Recover = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool AllowRetries = false);
public sealed record ProfileWorkerError(string Code, string Message);

public static class ProfilePreviewHost
{
    public static int Run(string root, Guid id, Guid? attemptId = null, Func<IProfileAssociationBackend>? backendFactory = null)
    {
        var store = new ProfilePreviewStore(root);
        string directory = store.DirectoryFor(id);
        DisplayMutationLock? gate = null;
        FileStream? worker = null;
        ProfilePreviewTransaction? transaction = null;
        try
        {
            if (id == Guid.Empty) throw new InvalidDataException("Invalid preview request identity.");
            worker = PreviewWorkerFiles.TryLock(directory, "worker");
            if (worker is null) return PreviewWorkerFiles.JoinedExistingWorker;
            var request = ProfilePreviewStore.Read<ProfilePreviewRequest>(Path.Combine(directory, "request.json"));
            if (request.Id != id) throw new InvalidDataException("Invalid preview request identity.");
            if (!request.Recover && store.Find(id) is not null) return 0;
            gate = DisplayMutationLock.Acquire();
            transaction = new(backendFactory?.Invoke() ?? new WindowsProfileAssociationBackend(), store);
            if (request.Recover)
            {
                var recovered = transaction.Recover(store.Load(id));
                return recovered.NeedsRecovery ? 4 : 0;
            }
            // A lost worker must be resolved before another transaction can hide its outstanding recovery.
            if (store.Incomplete().Any(s => s.Id != id) || new GammaCalibrationStore(root).Sessions().Any(s => s.NeedsRecovery))
                throw new ProfilePreviewException("recovery_required", "An earlier profile preview needs recovery.");
            if (!OwnerAlive(request)) throw new ProfilePreviewException("owner_exited", "The requesting window has closed.");
            transaction.Begin(id, request.DisplayId, request.CandidateProfile, request.Seconds);
            while (transaction.Session?.Status == "previewing")
            {
                if (transaction.Tick(OwnerAlive(request)).Status != "previewing") break;
                if (store.Requested(id, "revert")) transaction.Revert();
                else if (store.Requested(id, "confirm")) transaction.Confirm();
                if (transaction.Session?.Status == "previewing") Thread.Sleep(150);
            }
            return transaction.Session?.NeedsRecovery == true ? 4 : 0;
        }
        catch (Exception ex)
        {
            if (transaction?.Session?.NeedsRecovery == true)
            {
                try { transaction.Revert("worker_error"); } catch (Exception) { /* Existing journal remains recoverable. */ }
            }
            var error = new ProfileWorkerError(ex is ProfilePreviewException p ? p.Code : "preview_failed", ex.Message);
            AtomicFile.Write(PreviewWorkerFiles.ErrorPath(directory, attemptId), JsonSerializer.SerializeToUtf8Bytes(error, ProfilePreviewStore.Json));
            return 4;
        }
        finally
        {
            gate?.Dispose();
            worker?.Dispose();
        }
    }

    private static bool OwnerAlive(ProfilePreviewRequest request)
    {
        if (request.OwnerProcessId is null) return true; // CLI request survives its caller until the bounded deadline.
        if (request.OwnerStartTicks is null) return false;
        try
        {
            using var owner = Process.GetProcessById(request.OwnerProcessId.Value);
            return !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == request.OwnerStartTicks.Value;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

public sealed class ProfilePreviewClient(string root, string workerExecutable)
{
    private readonly ProfilePreviewStore _store = new(root);
    public Task<ProfilePreviewSession> BeginAsync(string displayId, string candidate, int seconds = 30, bool tieToCurrentProcess = false,
        Guid? requestId = null)
    {
        if (seconds is < 10 or > 60) throw new ArgumentException("Preview timeout must be 10–60 seconds.");
        if (requestId == Guid.Empty || (requestId is not null && tieToCurrentProcess))
            throw new ArgumentException(new LocalizationService()["PreviewRequestIdOwnership"]);
        IccProfileInspector.ValidateFileName(candidate);
        using var owner = Process.GetCurrentProcess();
        var request = new ProfilePreviewRequest(requestId ?? Guid.NewGuid(), displayId, candidate, seconds,
            tieToCurrentProcess ? owner.Id : null, tieToCurrentProcess ? owner.StartTime.ToUniversalTime().Ticks : null,
            AllowRetries: requestId is not null);
        string requestPath = Path.Combine(_store.DirectoryFor(request.Id), "request.json");
        if (_store.Find(request.Id) is { } prior)
        {
            if (!request.AllowRetries) throw RequestConflict();
            VerifyRequest(requestPath, request);
            return Task.FromResult(prior);
        }
        try
        {
            AtomicFile.Write(requestPath, JsonSerializer.SerializeToUtf8Bytes(request, ProfilePreviewStore.Json), overwrite: false);
        }
        catch (IOException) when (request.AllowRetries && File.Exists(requestPath))
        {
            VerifyRequest(requestPath, request);
        }
        var existing = _store.Find(request.Id);
        return existing is null ? LaunchAsync(request.Id) : Task.FromResult(existing);
    }
    public ProfilePreviewSession Status(Guid id) => _store.Load(id);
    public async Task<ProfilePreviewSession> CommandAsync(Guid id, string action)
    {
        string directory = _store.DirectoryFor(id);
        var before = ReadCommandState();
        if (!NeedsWorker(before)) return before;
        _store.Request(id, action);
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(100);
            var state = ReadCommandState();
            if (!NeedsWorker(state)) return state;
        }
        throw new ProfilePreviewException("worker_not_responding", "The preview worker did not acknowledge the request. Check status and recover if necessary.");

        ProfilePreviewSession ReadCommandState()
        {
            var (state, workerRunning) = PreviewWorkerFiles.Observe(directory, () => _store.Load(id), NeedsWorker);
            if (!workerRunning && NeedsWorker(state))
                throw new ProfilePreviewException("recovery_required", new LocalizationService()["RecoveryMessage"]);
            return state;
        }

        static bool NeedsWorker(ProfilePreviewSession state) => state.NeedsRecovery && state.Status != "recovery-pending";
    }
    public Task<ProfilePreviewSession> RecoverAsync(Guid id) => Task.Run(() => new PreviewRecoveryService(root).RecoverProfile(id));

    private async Task<ProfilePreviewSession> LaunchAsync(Guid id)
    {
        if (!File.Exists(workerExecutable)) throw new FileNotFoundException("The independent preview worker is missing. Rebuild or reinstall DCCW++.", workerExecutable);
        Guid attemptId = Guid.NewGuid();
        string errorPath = PreviewWorkerFiles.ErrorPath(_store.DirectoryFor(id), attemptId);
        var start = new ProcessStartInfo(Path.GetFullPath(workerExecutable)) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(workerExecutable))! };
        foreach (string arg in new[] { "__profile-worker", "--session", id.ToString("D"), "--data-dir",
            Path.GetFullPath(root), "--attempt", attemptId.ToString("D") }) start.ArgumentList.Add(arg);
        using var worker = Process.Start(start) ?? throw new IOException("Cannot start profile preview worker.");
        var timer = Stopwatch.StartNew();
        do
        {
            if (File.Exists(errorPath))
            {
                var error = ProfilePreviewStore.Read<ProfileWorkerError>(errorPath);
                throw new ProfilePreviewException(error.Code, error.Message);
            }
            bool exited = worker.HasExited;
            var session = _store.Find(id);
            if (session is not null && (exited || session.Status is "previewing" or "recovery-pending" || !session.NeedsRecovery))
                return session;
            if (exited && worker.ExitCode != PreviewWorkerFiles.JoinedExistingWorker) break;
            await Task.Delay(75);
        } while (timer.Elapsed < TimeSpan.FromSeconds(10));
        // Never kill a worker that might be restoring an association. Its timeout remains authoritative.
        throw new ProfilePreviewException("worker_start_timeout", $"The preview worker did not become ready. Session: {id:D}.");
    }

    private static ProfilePreviewException RequestConflict() =>
        new("request_id_conflict", new LocalizationService()["ProfilePreviewRequestConflict"]);

    private static void VerifyRequest(string requestPath, ProfilePreviewRequest request)
    {
        if (!File.Exists(requestPath)) throw RequestConflict();
        var original = ProfilePreviewStore.Read<ProfilePreviewRequest>(requestPath);
        if (!original.AllowRetries || original.Recover || original.OwnerProcessId is not null || original.OwnerStartTicks is not null
            || original.Id != request.Id || original.Seconds != request.Seconds || original.DisplayId != request.DisplayId
            || !ProfilePreviewPolicy.EqualName(original.CandidateProfile, request.CandidateProfile))
            throw RequestConflict();
    }
}
