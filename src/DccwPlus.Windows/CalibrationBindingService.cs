using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;

namespace DccwPlus.Windows;

public sealed record CalibrationRestoreReport(int SchemaVersion, bool Applied, string Reason, DateTimeOffset Time,
    IReadOnlyList<BindingRestoreResult> Bindings, IReadOnlyList<InterruptedPreviewResult> Recoveries, string? Trigger = null);

public sealed class CalibrationBindingService(string root)
{
    private readonly CalibrationBindingStore _store = new(root);
    private readonly CalibrationCompletionService _completion = new(root);
    private readonly string _reportPath = Path.Combine(Path.GetFullPath(root), "startup-status.json");

    public IReadOnlyList<CalibrationBinding> List() => _store.List();

    public CalibrationBinding Remove(string persistenceKey)
    {
        using var gate = DisplayMutationLock.Acquire();
        return _store.Remove(persistenceKey, () => _completion.RetirePending(
            session => session.Original.PersistenceKey == persistenceKey, "binding_removed_by_user"));
    }

    public void DeletePreset(Guid id)
    {
        using var gate = DisplayMutationLock.Acquire();
        new PresetStore(root).Delete(id, () => _completion.RetirePending(
            session => session.UndoOf is null && session.Preset.Id == id, "preset_removed_by_user"));
    }

    public CalibrationBinding SetEnabled(string persistenceKey, bool enabled)
    {
        using var gate = DisplayMutationLock.Acquire();
        var binding = _store.List().SingleOrDefault(b => b.PersistenceKey == persistenceKey)
            ?? throw new KeyNotFoundException("No saved calibration is bound to that display.");
        if (binding.RestoreEnabled != enabled)
            _completion.RetirePending(session => session.UndoOf is not null && session.Original.PersistenceKey == persistenceKey
                && ProfilePreviewPolicy.EqualName(session.Original.ProfileName, binding.ProfileName), "binding_changed");
        var updated = binding with { RestoreEnabled = enabled };
        _store.Save(updated);
        return updated;
    }

    public CalibrationRestoreReport Restore(bool dryRun)
    {
        using var gate = DisplayMutationLock.TryAcquire();
        if (gate is null) return Report("preview_busy");

        var recovery = new PreviewRecoveryService(root);
        if (dryRun && recovery.List().Any(item => item.Kind != "binding")) return Report("recovery_required");
        var recoveryReport = dryRun ? null : recovery.RecoverLocked();
        var recoveries = recoveryReport?.Sessions ?? [];
        if (recoveryReport?.Reason == "preview_busy") return Report("preview_busy", recoveries: recoveries);
        if (recoveries.Any(r => r.NeedsRecovery && r.Kind != "binding")) return Report("recovery_required", recoveries: recoveries);

        var gammaStore = new GammaCalibrationStore(root);
        var backend = new WindowsGammaCalibrationBackend(root);
        var bindings = _store.List().Where(b => b.RestoreEnabled).ToArray();
        if (bindings.Length == 0) return Report("no_bindings_enabled", recoveries: recoveries);
        var restorer = new CalibrationBindingRestorer(new WindowsDisplayService(), backend, gammaStore,
            InstalledProfileReader.ReadSha256, new PresetStore(root).Get);
        var results = new List<BindingRestoreResult>(bindings.Length);
        foreach (var binding in bindings)
        {
            results.Add(restorer.Restore(binding, dryRun));
            // Stop if a restore created a new incomplete transaction.
            if (!dryRun && gammaStore.Sessions().Any(s => s.NeedsRecovery))
                return Report("recovery_required", results, recoveries);
        }
        return Report(dryRun ? "restore_plan" : "restore_finished", results, recoveries);
    }

    public CalibrationRestoreReport RunStartup(bool enabled, string trigger)
    {
        CalibrationRestoreReport report;
        try { report = enabled ? Restore(dryRun: false) : Report("startup_disabled"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProfilePreviewException or JsonException or InvalidDataException)
        {
            report = Report(ex is ProfilePreviewException p ? p.Code : "restore_data_unavailable");
        }
        report = report with { Trigger = trigger };
        AtomicFile.Write(_reportPath, JsonSerializer.SerializeToUtf8Bytes(report, ProfilePreviewStore.Json));
        return report;
    }

    public CalibrationRestoreReport? LastStartupReport()
    {
        try
        {
            return ProfilePreviewStore.Read<CalibrationRestoreReport>(_reportPath) is { SchemaVersion: 1, Bindings: not null, Recoveries: not null } report
                ? report : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static CalibrationRestoreReport Report(string reason, IReadOnlyList<BindingRestoreResult>? bindings = null,
        IReadOnlyList<InterruptedPreviewResult>? recoveries = null) =>
        new(1, bindings?.Any(b => b.Status == "restored") == true, reason, DateTimeOffset.UtcNow, bindings ?? [], recoveries ?? []);
}
