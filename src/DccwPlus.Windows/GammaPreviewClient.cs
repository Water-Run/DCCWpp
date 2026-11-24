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

    public GammaCalibrationSession? FindByRequestId(Guid requestId, string displayId, Guid presetId, int seconds) =>
        FindByRequestId(requestId, seconds, displayId, presetId, undoOf: null);

    private GammaCalibrationSession? FindByRequestId(Guid requestId, int seconds, string? displayId, Guid? presetId, Guid? undoOf)
    {
        var original = ReadRequest(requestId);
        if (original is null)
        {
            if (_store.Find(requestId) is null) return null;
            original = ReadRequest(requestId); // Reservation can appear between the two reads.
        }
        EnsureSameRequest(original, requestId, seconds, displayId, presetId, undoOf);
        return _store.Find(requestId);
    }

    public Task<GammaCalibrationSession> BeginAsync(string displayId, CalibrationPreset preset, int seconds = 30,
        bool windowOwned = false, Guid? requestId = null)
    {
        if (seconds is < 10 or > 60) throw new ArgumentException("Preview timeout must be 10–60 seconds.");
        if (requestId == Guid.Empty || (requestId is not null && windowOwned))
            throw new ArgumentException(new LocalizationService()["PreviewRequestIdOwnership"]);
        PresetCodec.Validate(preset);
        using var owner = Process.GetCurrentProcess();
        var request = new GammaPreviewRequest(requestId ?? Guid.NewGuid(), displayId, preset, seconds,
            windowOwned ? owner.Id : null, windowOwned ? owner.StartTime.ToUniversalTime().Ticks : null,
            AllowRetries: requestId is not null);
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
            EnsureSameRequest(ReadRequest(request.Id), request.Id, request.Seconds, request.DisplayId, request.Preset?.Id, request.UndoOf);
        }
        var existing = _store.Find(request.Id);
        return existing is null ? LaunchAsync(request.Id, recover: false) : Task.FromResult(existing);
    }

    public GammaCalibrationSession Status(Guid id) => _store.Load(id);

    public Task<GammaCalibrationSession> BeginUndoAsync(Guid calibrationId, int seconds = 30, bool windowOwned = false,
        Guid? requestId = null)
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
            AllowRetries: requestId is not null, UndoOf: calibrationId);
        return ReserveAndLaunch(request);
    }

    public Task<GammaCalibrationSession> RecoverAsync(Guid id)
    {
        var session = _store.Load(id);
        return session.NeedsRecovery ? LaunchAsync(id, recover: true) : Task.FromResult(session);
    }

    public async Task<GammaCalibrationSession> CommandAsync(Guid id, string action, CalibrationPreset? preset = null, bool showOriginal = false)
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
        var session = _store.Load(id);
        if (session.Status != "previewing") return session;
        var command = new GammaPreviewCommand(Guid.NewGuid(), action, preset, showOriginal);
        AtomicFile.Write(commandPath, JsonSerializer.SerializeToUtf8Bytes(command, ProfilePreviewStore.Json));
        return await WaitForCommandAsync(id, command, reportError: true);
    }

    private async Task<GammaCalibrationSession> WaitForCommandAsync(Guid id, GammaPreviewCommand command, bool reportError)
    {
        string path = Path.Combine(_store.DirectoryFor(id), "ack.json");
        var timer = Stopwatch.StartNew();
        do
        {
            if (File.Exists(path))
            {
                var ack = ProfilePreviewStore.Read<GammaPreviewAck>(path);
                if (ack.CommandId == command.Id)
                {
                    if (reportError && ack.ErrorCode is not null)
                        throw new ProfilePreviewException(ack.ErrorCode, ack.Message ?? ack.ErrorCode);
                    return _store.Load(id);
                }
            }
            var session = _store.Load(id);
            if (session.Status == "recovery-pending") return session;
            if (!session.NeedsRecovery && !(reportError && command.Action == "confirm" && session.Status == "confirmed"))
                return session;
            await Task.Delay(75);
        } while (timer.Elapsed < WaitLimit);
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
        string errorPath = PreviewWorkerFiles.ErrorPath(_store.DirectoryFor(id), attemptId);
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
            if (File.Exists(errorPath))
            {
                var error = ProfilePreviewStore.Read<ProfileWorkerError>(errorPath);
                throw new ProfilePreviewException(error.Code, error.Message);
            }
            bool exited = worker.HasExited;
            var session = _store.Find(id);
            if (session is not null && (recover ? exited : exited || session.Status is "previewing" or "recovery-pending"))
                return session;
            if (exited && worker.ExitCode != PreviewWorkerFiles.JoinedExistingWorker) break;
            await Task.Delay(75);
        } while (timer.Elapsed < WaitLimit);
        throw new ProfilePreviewException("worker_start_timeout", $"The calibration worker did not become ready. Query session {id:D} before retrying.");
    }

    private GammaPreviewRequest? ReadRequest(Guid id)
    {
        string path = Path.Combine(_store.DirectoryFor(id), "request.json");
        return File.Exists(path) ? ProfilePreviewStore.Read<GammaPreviewRequest>(path) : null;
    }

    private static void EnsureSameRequest(GammaPreviewRequest? original, Guid id, int seconds, string? displayId, Guid? presetId, Guid? undoOf)
    {
        if (original is not { AllowRetries: true, Recover: false, OwnerPid: null, OwnerStartTicks: null }
            || original.Id != id || original.Seconds != seconds || original.UndoOf != undoOf || original.Preset?.Id != presetId
            || (displayId is not null && original.DisplayId != displayId))
            throw RequestConflict();
    }

    private static ProfilePreviewException RequestConflict() =>
        new("request_id_conflict", "This request ID belongs to a different calibration request.");
}

internal static class PreviewWorkerFiles
{
    // A redundant worker exits without publishing an error to the active worker's client.
    public const int JoinedExistingWorker = 10;

    public static string ErrorPath(string directory, Guid? attemptId) => Path.Combine(directory,
        attemptId is Guid id ? $"attempt-{id:N}.error.json" : "error.json");

    public static FileStream? TryLock(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, $"{name}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return null; }
    }
}
