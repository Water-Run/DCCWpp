using System.Diagnostics;
using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Storage;

namespace DccwPlus.Windows;

public sealed record ProfilePreviewRequest(Guid Id, string DisplayId, string CandidateProfile, int Seconds,
    int? OwnerProcessId = null, long? OwnerStartTicks = null, bool Recover = false);
public sealed record ProfileWorkerError(string Code, string Message);

public static class ProfilePreviewHost
{
    public static int Run(string root, Guid id)
    {
        var store = new ProfilePreviewStore(root);
        string directory = store.DirectoryFor(id);
        DisplayMutationLock? gate = null;
        ProfilePreviewTransaction? transaction = null;
        try
        {
            gate = DisplayMutationLock.Acquire();
            var request = ProfilePreviewStore.Read<ProfilePreviewRequest>(Path.Combine(directory, "request.json"));
            if (request.Id != id || id == Guid.Empty) throw new InvalidDataException("Invalid preview request identity.");
            transaction = new(new WindowsProfileAssociationBackend(), store);
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
                transaction.Tick(OwnerAlive(request));
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
            AtomicFile.Write(Path.Combine(directory, "error.json"), JsonSerializer.SerializeToUtf8Bytes(error, ProfilePreviewStore.Json));
            return 4;
        }
        finally { gate?.Dispose(); }
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
    public async Task<ProfilePreviewSession> BeginAsync(string displayId, string candidate, int seconds = 30, bool tieToCurrentProcess = false)
    {
        if (seconds is < 10 or > 60) throw new ArgumentException("Preview timeout must be 10–60 seconds.");
        IccProfileInspector.ValidateFileName(candidate);
        using var owner = Process.GetCurrentProcess();
        var request = new ProfilePreviewRequest(Guid.NewGuid(), displayId, candidate, seconds,
            tieToCurrentProcess ? owner.Id : null, tieToCurrentProcess ? owner.StartTime.ToUniversalTime().Ticks : null);
        return await LaunchAsync(request);
    }
    public ProfilePreviewSession Status(Guid id) => _store.Load(id);
    public async Task<ProfilePreviewSession> CommandAsync(Guid id, string action)
    {
        var before = _store.Load(id);
        if (!before.NeedsRecovery) return before;
        _store.Request(id, action);
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(100);
            var state = _store.Load(id);
            if (state.Status != "previewing" && state.Status != "applying" && state.Status != "prepared" && state.Status != "recovering") return state;
        }
        throw new ProfilePreviewException("worker_not_responding", "The preview worker did not acknowledge the request. Check status and recover if necessary.");
    }
    public Task<ProfilePreviewSession> RecoverAsync(Guid id) => Task.Run(() => new PreviewRecoveryService(root).RecoverProfile(id));

    private async Task<ProfilePreviewSession> LaunchAsync(ProfilePreviewRequest request)
    {
        if (!File.Exists(workerExecutable)) throw new FileNotFoundException("The independent preview worker is missing. Rebuild or reinstall DCCW++.", workerExecutable);
        string directory = _store.DirectoryFor(request.Id);
        string errorPath = Path.Combine(directory, "error.json");
        AtomicFile.Write(Path.Combine(directory, "request.json"), JsonSerializer.SerializeToUtf8Bytes(request, ProfilePreviewStore.Json));
        var start = new ProcessStartInfo(Path.GetFullPath(workerExecutable)) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(workerExecutable))! };
        foreach (string arg in new[] { "__profile-worker", "--session", request.Id.ToString("D"), "--data-dir", Path.GetFullPath(root) }) start.ArgumentList.Add(arg);
        using var worker = Process.Start(start) ?? throw new IOException("Cannot start profile preview worker.");
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(100);
            if (File.Exists(errorPath))
            {
                var error = ProfilePreviewStore.Read<ProfileWorkerError>(errorPath);
                throw new ProfilePreviewException(error.Code, error.Message);
            }
            if (File.Exists(Path.Combine(directory, "state.json")))
            {
                var session = _store.Load(request.Id);
                if (session.Status is "previewing" or "reverted" or "recovery-pending") return session;
            }
            if (worker.HasExited) break;
        }
        // Never kill a worker that might be restoring an association. Its timeout remains authoritative.
        throw new ProfilePreviewException("worker_start_timeout", $"The preview worker did not become ready. Session: {request.Id:D}.");
    }
}
