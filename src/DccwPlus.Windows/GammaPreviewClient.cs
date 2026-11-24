using System.Diagnostics;
using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;

namespace DccwPlus.Windows;

public sealed class GammaPreviewClient(string root, string workerExecutable)
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private readonly GammaCalibrationStore _store = new(root);

    public void CheckCanPreview(string displayId, CalibrationPreset preset)
    {
        using var gate = DisplayMutationLock.Acquire();
        GammaPreviewHost.RequireNoInterruptedPreview(root);
        _ = new WindowsGammaCalibrationBackend(root).CheckPreviewStart(displayId, preset);
    }

    public GammaCalibrationSession? FindByRequestId(Guid requestId, string displayId, Guid presetId, int seconds) =>
        FindByRequestId(requestId, seconds, displayId, presetId, undoOf: null);

    private GammaCalibrationSession? FindByRequestId(Guid requestId, int seconds, string? displayId, Guid? presetId,
        Guid? undoOf, SdrCalibration? inlineSettings = null)
    {
        var original = ReadRequest(requestId);
        if (original is null)
        {
            if (_store.Find(requestId) is null) return null;
            original = ReadRequest(requestId); // Reservation can appear between the two reads.
        }
        EnsureSameRequest(original, requestId, seconds, displayId, presetId, undoOf, inlineSettings);
        return _store.Find(requestId);
    }

    public Task<GammaCalibrationSession> BeginAsync(string displayId, CalibrationPreset preset, int seconds = 30,
        bool windowOwned = false, Guid? requestId = null, bool inlineParameters = false)
    {
        if (seconds is < 10 or > 60) throw new ArgumentException("Preview timeout must be 10–60 seconds.");
        if (requestId == Guid.Empty || (requestId is not null && windowOwned))
            throw new ArgumentException(new LocalizationService()["PreviewRequestIdOwnership"]);
        PresetCodec.Validate(preset);
        if (inlineParameters && requestId is Guid replayId
            && FindByRequestId(replayId, seconds, displayId, presetId: null, undoOf: null, inlineSettings: preset.Calibration) is { } prior)
            return Task.FromResult(prior);
        using var owner = Process.GetCurrentProcess();
        var request = new GammaPreviewRequest(requestId ?? Guid.NewGuid(), displayId, preset, seconds,
            windowOwned ? owner.Id : null, windowOwned ? owner.StartTime.ToUniversalTime().Ticks : null,
            AllowRetries: requestId is not null, InlineParameters: inlineParameters);
        return ReserveAndLaunch(request);
    }

    private Task<GammaCalibrationSession> ReserveAndLaunch(GammaPreviewRequest request)
    {
        string directory = _store.DirectoryFor(request.Id);
        string requestPath = Path.Combine(directory, "request.json");
        if (_store.Find(request.Id) is not null && !File.Exists(requestPath))
            throw RequestConflict();
        try
        {
            AtomicFile.Write(requestPath, JsonSerializer.SerializeToUtf8Bytes(request, ProfilePreviewStore.Json), overwrite: false);
        }
        catch (IOException) when (request.AllowRetries && File.Exists(requestPath))
        {
            EnsureSameRequest(ReadRequest(request.Id), request.Id, request.Seconds, request.DisplayId, request.Preset?.Id,
                request.UndoOf, request.InlineParameters ? request.Preset!.Calibration : null);
        }
        var existing = _store.Find(request.Id);
        return existing is null ? LaunchAsync(request.Id, recover: false) : Task.FromResult(existing);
    }

    public GammaCalibrationSession Status(Guid id) => _store.Load(id);

    public (GammaCalibrationSession Session, bool WorkerRunning) Observe(Guid id) =>
        PreviewWorkerFiles.Observe(_store.DirectoryFor(id), () => _store.Load(id),
            session => session.NeedsRecovery && session.Status != "recovery-pending");

    public Task<GammaCalibrationSession> BeginUndoAsync(Guid calibrationId, int seconds = 30, bool windowOwned = false,
        Guid? requestId = null, string? expectedDisplayId = null)
    {
        if (seconds is < 10 or > 60) throw new ArgumentException("Undo preview timeout must be 10–60 seconds.");
        if (requestId == Guid.Empty || (requestId is not null && windowOwned))
            throw new ArgumentException(new LocalizationService()["PreviewRequestIdOwnership"]);
        if (requestId is Guid replayId && FindByRequestId(replayId, seconds, displayId: null, presetId: null, undoOf: calibrationId) is { } prior)
            return Task.FromResult(prior);
        var saved = _store.Load(calibrationId);
        CalibrationUndoPolicy.VerifySource(saved);
        using var owner = Process.GetCurrentProcess();
        var request = new GammaPreviewRequest(requestId ?? Guid.NewGuid(), saved.Original.DisplayId, null, seconds,
            windowOwned ? owner.Id : null, windowOwned ? owner.StartTime.ToUniversalTime().Ticks : null,
            AllowRetries: requestId is not null, UndoOf: calibrationId, ExpectedUndoDisplayId: windowOwned ? expectedDisplayId : null);
        return ReserveAndLaunch(request);
    }

    public Task<GammaCalibrationSession> RecoverAsync(Guid id)
    {
        var session = _store.Load(id);
        if (session.NeedsBindingCompletion) return Task.Run(() => new CalibrationCompletionService(root).Recover(id));
        return session.NeedsRecovery ? LaunchAsync(id, recover: true) : Task.FromResult(session);
    }

    public Task<GammaCalibrationSession> CommandAsync(Guid id, string action, CalibrationPreset? preset = null, bool showOriginal = false)
        => SendCommandAsync(id, action, preset, showOriginal);

    public Task<GammaCalibrationSession> EditAsync(Guid id, Func<CalibrationPreset, CalibrationPreset> edit, bool showOriginal)
        => SendCommandAsync(id, "update", null, showOriginal, edit);

    private async Task<GammaCalibrationSession> SendCommandAsync(Guid id, string action, CalibrationPreset? preset,
        bool showOriginal, Func<CalibrationPreset, CalibrationPreset>? edit = null)
    {
        var first = _store.Load(id);
        if (!first.NeedsRecovery || first.Status == "recovery-pending") return first;
        string directory = _store.DirectoryFor(id);
        using var sender = await AcquireCommandLockAsync(directory);
        string commandPath = Path.Combine(directory, "command.json");
        // If a caller exited after submitting a command, wait for it instead of replacing it.
        if (File.Exists(commandPath))
        {
            var pending = ProfilePreviewStore.Read<GammaPreviewCommand>(commandPath);
            await WaitForCommandAsync(id, pending, reportError: false);
        }
        var (session, workerRunning) = Observe(id);
        if (!workerRunning && session.NeedsRecovery && session.Status != "recovery-pending") throw PreviewWorkerFiles.RecoveryRequired();
        if (session.Status != "previewing") return session;
        var command = new GammaPreviewCommand(Guid.NewGuid(), action, edit is null ? preset : edit(session.Preset), showOriginal);
        AtomicFile.Write(commandPath, JsonSerializer.SerializeToUtf8Bytes(command, ProfilePreviewStore.Json));
        return await WaitForCommandAsync(id, command, reportError: true);
    }

    private async Task<GammaCalibrationSession> WaitForCommandAsync(Guid id, GammaPreviewCommand command, bool reportError)
    {
        string path = Path.Combine(_store.DirectoryFor(id), "ack.json");
        var timer = Stopwatch.StartNew();
        bool confirming = command.Action == "confirm";
        do
        {
            if (File.Exists(path))
            {
                var ack = ProfilePreviewStore.Read<GammaPreviewAck>(path);
                if (ack.CommandId == command.Id)
                {
                    if (reportError && ack.ErrorCode is not null)
                    {
                        if (confirming && _store.Load(id) is { Status: "confirmed" } committed) return committed;
                        throw new ProfilePreviewException(ack.ErrorCode, ack.Message ?? ack.ErrorCode);
                    }
                    return _store.Load(id);
                }
            }
            var (session, workerRunning) = Observe(id);
            if (session.Status == "recovery-pending") return session;
            // A confirmed outcome can precede the acknowledgement and the local binding write.
            if (!session.NeedsRecovery)
            {
                if (!reportError || !confirming || session.Status != "confirmed") return session;
                if (!PreviewWorkerFiles.IsLocked(_store.DirectoryFor(id), "worker")) return _store.Load(id);
            }
            if (session.NeedsRecovery && !workerRunning) throw PreviewWorkerFiles.RecoveryRequired();
            await Task.Delay(75);
        } while (timer.Elapsed < WaitLimit);
        // The display commit is durable even if local completion has not produced a receipt.
        if (confirming && _store.Load(id) is { Status: "confirmed" } completed) return completed;
        throw new ProfilePreviewException("worker_not_responding", "The calibration worker did not acknowledge the request. Check the session status before retrying.");
    }

    private static async Task<FileStream> AcquireCommandLockAsync(string directory)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            if (PreviewWorkerFiles.TryLock(directory, "command") is { } handle) return handle;
            await Task.Delay(75);
        } while (timer.Elapsed < WaitLimit);
        throw new ProfilePreviewException("command_busy", "Another command is already waiting for this calibration session.");
    }

    private async Task<GammaCalibrationSession> LaunchAsync(Guid id, bool recover)
    {
        if (!File.Exists(workerExecutable)) throw new FileNotFoundException("The calibration worker is missing. Rebuild or reinstall DCCW++.", workerExecutable);
        Guid attemptId = Guid.NewGuid();
        string directory = _store.DirectoryFor(id);
        string errorPath = PreviewWorkerFiles.ErrorPath(directory, attemptId);
        var start = new ProcessStartInfo(Path.GetFullPath(workerExecutable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(workerExecutable))!
        };
        foreach (string arg in new[] { "__gamma-worker", "--session", id.ToString("D"), "--data-dir",
            Path.GetFullPath(root), "--attempt", attemptId.ToString("D") })
            start.ArgumentList.Add(arg);
        if (recover) start.ArgumentList.Add("--recover");

        using var worker = Process.Start(start) ?? throw new IOException("Cannot start the independent calibration worker.");
        var timer = Stopwatch.StartNew();
        do
        {
            int? exitCode = PreviewWorkerFiles.ReadLaunchExitCode(worker, errorPath);
            if (recover)
            {
                if (exitCode is not null && _store.Find(id) is { } recovered) return recovered;
            }
            else if (PreviewWorkerFiles.ReadReadySession(directory, () => _store.Find(id),
                session => session.NeedsRecovery && session.Status != "recovery-pending",
                session => session.Status == "previewing" ? session.ExpiresAt : null,
                launchExited: exitCode is not null) is { } ready) return ready;
            if (exitCode is not (null or PreviewWorkerFiles.JoinedExistingWorker)) break;
            await Task.Delay(75);
        } while (timer.Elapsed < WaitLimit);
        throw new ProfilePreviewException("worker_start_timeout", $"The calibration worker did not become ready. Query session {id:D} before retrying.");
    }

    private GammaPreviewRequest? ReadRequest(Guid id)
    {
        string path = Path.Combine(_store.DirectoryFor(id), "request.json");
        return File.Exists(path) ? ProfilePreviewStore.Read<GammaPreviewRequest>(path) : null;
    }

    private static void EnsureSameRequest(GammaPreviewRequest? original, Guid id, int seconds, string? displayId,
        Guid? presetId, Guid? undoOf, SdrCalibration? inlineSettings = null)
    {
        if (original is not { AllowRetries: true, Recover: false, OwnerPid: null, OwnerStartTicks: null }
            || original.Id != id || original.Seconds != seconds || original.UndoOf != undoOf
            || (displayId is not null && original.DisplayId != displayId)
            || original.InlineParameters != (inlineSettings is not null)
            || (inlineSettings is null ? original.Preset?.Id != presetId : original.Preset?.Calibration != inlineSettings))
            throw RequestConflict();
    }

    private static ProfilePreviewException RequestConflict() =>
        new("request_id_conflict", "This request ID belongs to a different calibration request.");
}

internal static class PreviewWorkerFiles
{
    // A redundant worker exits without publishing an error to the active worker's client.
    public const int JoinedExistingWorker = 10;

    public static FileStream AcquireForRecovery(string directory) => TryLock(directory, "worker")
        ?? throw new ProfilePreviewException("preview_busy", new LocalizationService()["RecoveryBusy"]);

    public static ProfilePreviewException RecoveryRequired() =>
        new("recovery_required", new LocalizationService()["RecoveryMessage"]);

    public static int? ReadLaunchExitCode(Process worker, string errorPath)
    {
        // Check for the receipt after sampling process exit, since the worker publishes it before exiting.
        int? exitCode = worker.HasExited ? worker.ExitCode : null;
        if (File.Exists(errorPath))
        {
            var error = ProfilePreviewStore.Read<ProfileWorkerError>(errorPath);
            throw new ProfilePreviewException(error.Code, error.Message);
        }
        return exitCode;
    }

    public static T? ReadReadySession<T>(string directory, Func<T?> load, Func<T, bool> needsWorker,
        Func<T, DateTimeOffset?> previewDeadline, bool launchExited) where T : class
    {
        var (session, workerRunning) = Observe(directory, load, state => state is not null && needsWorker(state));
        if (session is null) return null;
        // A rollback can be journaled before the worker writes its failure receipt.
        if (!needsWorker(session)) return launchExited ? session : null;
        if (workerRunning)
            return previewDeadline(session) is { } deadline && DateTimeOffset.UtcNow < deadline ? session : null;
        if (launchExited) throw RecoveryRequired();
        return null;
    }

    public static (T State, bool WorkerRunning) Observe<T>(string directory, Func<T> load, Func<T, bool> mayHaveWorker)
    {
        var state = load();
        if (!mayHaveWorker(state)) return (state, false);
        if (IsLocked(directory, "worker")) return (state, true);

        // The final journal write may land between our initial read and the worker releasing its lock.
        return (load(), false);
    }

    public static string ErrorPath(string directory, Guid? attemptId) => Path.Combine(directory,
        attemptId is Guid id ? $"attempt-{id:N}.error.json" : "error.json");

    public static FileStream? TryLock(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, $"{name}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return null; }
    }

    public static bool IsLocked(string directory, string name)
    {
        try
        {
            using var probe = new FileStream(Path.Combine(directory, $"{name}.lock"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
