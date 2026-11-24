using System.Diagnostics;
using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;

namespace DccwPlus.Windows;

public sealed record GammaPreviewRequest(Guid Id, string DisplayId, CalibrationPreset? Preset, int Seconds,
    int? OwnerPid = null, long? OwnerStartTicks = null, bool Recover = false, bool AllowRetries = false, Guid? UndoOf = null);
public sealed record GammaPreviewCommand(Guid Id, string Action, CalibrationPreset? Preset = null, bool ShowOriginal = false);
public sealed record GammaPreviewAck(Guid CommandId, string? ErrorCode, string? Message);

public static class GammaPreviewHost
{
    // A redundant worker exits without publishing an error to the active worker's client.
    internal const int JoinedExistingWorker = 10;

    public static int Run(string root, Guid id, Func<IGammaCalibrationBackend>? backendFactory = null,
        Guid? attemptId = null, bool recover = false)
    {
        var store = new GammaCalibrationStore(root);
        string directory = store.DirectoryFor(id);
        DisplayMutationLock? gate = null;
        GammaCalibrationTransaction? transaction = null;
        try
        {
            if (id == Guid.Empty) throw new InvalidDataException("Invalid calibration request.");
            using var worker = GammaPreviewFiles.TryLock(directory, "worker");
            if (worker is null)
            {
                if (!recover) return JoinedExistingWorker;
                throw new ProfilePreviewException("preview_busy", "Another process is handling this calibration session.");
            }
            var request = recover ? null : ProfilePreviewStore.Read<GammaPreviewRequest>(Path.Combine(directory, "request.json"));
            if (request is not null && request.Id != id) throw new InvalidDataException("Invalid calibration request.");
            bool recovering = recover || request?.Recover == true;
            // A journal is created before the first display write. An existing journal can only be inspected or recovered.
            if (!recovering && store.Find(id) is not null) return 0;
            gate = DisplayMutationLock.Acquire();
            var backend = backendFactory?.Invoke() ?? new WindowsGammaCalibrationBackend(root);
            transaction = new(backend, store);
            if (recovering) return transaction.Recover(store.Load(id)).NeedsRecovery ? 4 : 0;
            if (request is null) throw new InvalidDataException("A calibration request is required.");
            if (store.Sessions().Any(s => s.Id != id && s.NeedsRecovery) || new ProfilePreviewStore(root).Incomplete().Count > 0)
                throw new ProfilePreviewException("recovery_required", "An interrupted preview must be recovered first.");
            if (!OwnerAlive(request)) throw new ProfilePreviewException("owner_exited", "The calibration window has closed.");
            if (request.UndoOf is Guid calibrationId)
            {
                var saved = store.Load(calibrationId);
                if (backend is not WindowsGammaCalibrationBackend windows || request.AllowRetries || saved.Original.DisplayId != request.DisplayId)
                    throw new InvalidDataException("Invalid undo request.");
                var current = windows.CheckUndoStart(saved);
                transaction.Begin(id, saved.Preset, request.DisplayId, request.Seconds, saved.Original.Ramp, current, calibrationId);
            }
            else
                transaction.Begin(id, request.Preset ?? throw new InvalidDataException("A calibration preset is required."),
                    request.DisplayId, request.Seconds, windowOwned: request.OwnerPid is not null);
            Guid lastCommand = Guid.Empty;
            while (transaction.Session?.Status == "previewing")
            {
                transaction.Tick(OwnerAlive(request));
                string commandPath = Path.Combine(directory, "command.json");
                if (transaction.Session?.Status == "previewing" && File.Exists(commandPath))
                {
                    var command = ProfilePreviewStore.Read<GammaPreviewCommand>(commandPath);
                    if (command.Id != Guid.Empty && command.Id != lastCommand)
                    {
                        lastCommand = command.Id;
                        try
                        {
                            switch (command.Action)
                            {
                                case "update": transaction.Update(command.Preset ?? throw new InvalidDataException("Preset is required."), command.ShowOriginal, renew: true); break;
                                case "heartbeat": transaction.Renew(); break;
                                case "review": transaction.SetReviewMode(true); break;
                                case "edit": transaction.SetReviewMode(false); break;
                                case "confirm":
                                    var confirmed = transaction.Confirm();
                                    if (confirmed.Status == "confirmed" && backendFactory is null)
                                        SaveBinding(root, store, confirmed);
                                    break;
                                case "revert": transaction.Revert(); break;
                                default: throw new InvalidDataException("Unknown calibration action.");
                            }
                            Ack(directory, new(command.Id, null, null));
                        }
                        catch (Exception ex) { Ack(directory, new(command.Id, ex is ProfilePreviewException p ? p.Code : "calibration_failed", ex.Message)); }
                    }
                }
                if (transaction.Session?.Status == "previewing") Thread.Sleep(150);
            }
            return transaction.Session?.NeedsRecovery == true ? 4 : 0;
        }
        catch (Exception ex)
        {
            if (transaction?.Session?.NeedsRecovery == true)
            { try { transaction.Revert("worker_error"); } catch (Exception) { } }
            AtomicFile.Write(GammaPreviewFiles.ErrorPath(directory, attemptId), JsonSerializer.SerializeToUtf8Bytes(
                new ProfileWorkerError(ex is ProfilePreviewException p ? p.Code : "calibration_failed", ex.Message), ProfilePreviewStore.Json));
            return 4;
        }
        finally { gate?.Dispose(); }
    }
    private static void SaveBinding(string root, GammaCalibrationStore store, GammaCalibrationSession confirmed)
    {
        string? reason = null;
        string status = "saved";
        try
        {
            var bindings = new CalibrationBindingService(root);
            if (confirmed.UndoOf is Guid calibrationId)
                status = bindings.RemoveUndoneBinding(store.Load(calibrationId)) ? "removed" : "unchanged";
            else
                bindings.SaveConfirmed(confirmed);
        }
        catch (Exception ex) when (ex is ProfilePreviewException or PresetOperationException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            reason = ex switch
            {
                ProfilePreviewException preview => preview.Code,
                PresetOperationException preset => preset.Code,
                _ => "binding_save_failed"
            };
        }
        store.Save(confirmed with { BindingStatus = reason is null ? status : "failed", BindingReasonCode = reason });
    }
    private static void Ack(string directory, GammaPreviewAck ack) => AtomicFile.Write(Path.Combine(directory, "ack.json"), JsonSerializer.SerializeToUtf8Bytes(ack, ProfilePreviewStore.Json));
    private static bool OwnerAlive(GammaPreviewRequest request)
    {
        if (request.OwnerPid is null) return true;
        try
        {
            using var owner = Process.GetProcessById(request.OwnerPid.Value);
            return !owner.HasExited && request.OwnerStartTicks == owner.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
