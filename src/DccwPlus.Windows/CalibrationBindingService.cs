using System.Security.Cryptography;
using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;

namespace DccwPlus.Windows;

public sealed record InterruptedPreviewResult(Guid SessionId, string Kind, string Status, string ReasonCode, bool NeedsRecovery);
public sealed record CalibrationRestoreReport(int SchemaVersion, bool Applied, string Reason, DateTimeOffset Time,
    IReadOnlyList<BindingRestoreResult> Bindings, IReadOnlyList<InterruptedPreviewResult> Recoveries, string? Trigger = null);

public sealed class CalibrationBindingService(string root)
{
    private readonly CalibrationBindingStore _store = new(root);
    private readonly string _reportPath = Path.Combine(Path.GetFullPath(root), "startup-status.json");

    public IReadOnlyList<CalibrationBinding> List() => _store.List();

    public CalibrationBinding Remove(string persistenceKey)
    {
        using var gate = DisplayMutationLock.Acquire();
        return _store.Remove(persistenceKey);
    }

    public void DeletePreset(Guid id)
    {
        using var gate = DisplayMutationLock.Acquire();
        new PresetStore(root).Delete(id);
    }

    // Called with the display mutation lock after the previous ICC and curve were restored.
    internal bool RemoveUndoneBinding(GammaCalibrationSession saved)
    {
        var binding = _store.List().SingleOrDefault(b => b.PersistenceKey == saved.Original.PersistenceKey
            && ProfilePreviewPolicy.EqualName(b.ProfileName, saved.InstalledProfile));
        if (binding is null) return false;
        _store.Remove(binding.PersistenceKey);
        return true;
    }

    // The caller holds the display mutation lock through both confirmation and binding save.
    public void SaveConfirmed(GammaCalibrationSession session)
    {
        if (session.Status != "confirmed" || session.InstalledProfile is null || session.IsBindingRestore)
            throw new InvalidOperationException("A newly confirmed calibration is required.");
        var presets = new PresetStore(root);
        try { _ = presets.Get(session.Preset.Id); }
        catch (FileNotFoundException) { presets.Add(session.Preset); }
        catch (DirectoryNotFoundException) { presets.Add(session.Preset); }
        var displays = new WindowsDisplayService().Enumerate();
        var display = displays.SingleOrDefault(d => d.Id == session.Original.DisplayId);
        if (display?.PersistenceKey is null || display.PersistenceKind is null
            || session.Original.PersistenceKey is null || session.Original.PersistenceKey != display.PersistenceKey
            || displays.Count(d => d.PersistenceKey == display.PersistenceKey) != 1)
            throw new ProfilePreviewException("display_identity_unavailable", "A unique persistent display identity is required.");
        var saved = new WindowsGammaCalibrationBackend(root).Read(display.Id);
        if (!GammaCalibrationTransaction.SameTarget(session.Original, saved) || saved.ProfileScope != "current-user"
            || !ProfilePreviewPolicy.EqualName(saved.ProfileName, session.InstalledProfile))
            throw new ProfilePreviewException("profile_selection_changed", "The confirmed profile is no longer selected.");

        var old = _store.List().SingleOrDefault(b => b.PersistenceKey == display.PersistenceKey);
        _store.Save(new()
        {
            PersistenceKey = display.PersistenceKey,
            IdentityKind = display.PersistenceKind,
            DisplayName = display.Name,
            PresetId = session.Preset.Id,
            ProfileName = session.InstalledProfile,
            ProfileSha256 = HashInstalledProfile(session.InstalledProfile),
            Ramp = session.Candidate.Copy(),
            OriginalRamp = session.Original.Ramp.Copy(),
            RestoreEnabled = old?.RestoreEnabled ?? false,
            SavedAt = DateTimeOffset.UtcNow
        });
    }

    public CalibrationBinding SetEnabled(string persistenceKey, bool enabled)
    {
        using var gate = DisplayMutationLock.Acquire();
        var binding = _store.List().SingleOrDefault(b => b.PersistenceKey == persistenceKey)
            ?? throw new KeyNotFoundException("No saved calibration is bound to that display.");
        var updated = binding with { RestoreEnabled = enabled };
        _store.Save(updated);
        return updated;
    }

    public CalibrationRestoreReport Restore(bool dryRun)
    {
        using var gate = DisplayMutationLock.TryAcquire();
        if (gate is null) return Report("preview_busy");

        var gammaStore = new GammaCalibrationStore(root);
        var profileStore = new ProfilePreviewStore(root);
        var gamma = gammaStore.Sessions().Where(s => s.NeedsRecovery).ToArray();
        var profiles = profileStore.Incomplete();
        if (dryRun && (gamma.Length > 0 || profiles.Count > 0)) return Report("recovery_required");

        var backend = new WindowsGammaCalibrationBackend(root);
        List<InterruptedPreviewResult> recoveries = [];
        foreach (var interrupted in gamma)
        {
            var result = new GammaCalibrationTransaction(backend, gammaStore).Recover(interrupted);
            recoveries.Add(new(result.Id, "calibration", result.Status, result.ReasonCode, result.NeedsRecovery));
        }
        foreach (var interrupted in profiles)
        {
            var result = new ProfilePreviewTransaction(new WindowsProfileAssociationBackend(), profileStore).Recover(interrupted);
            recoveries.Add(new(result.Id, "profile", result.Status, result.ReasonCode, result.NeedsRecovery));
        }
        if (recoveries.Any(r => r.NeedsRecovery)) return Report("recovery_required", recoveries: recoveries);

        var bindings = _store.List().Where(b => b.RestoreEnabled).ToArray();
        if (bindings.Length == 0) return Report("no_bindings_enabled", recoveries: recoveries);
        var restorer = new CalibrationBindingRestorer(new WindowsDisplayService(), backend, gammaStore, HashInstalledProfile, new PresetStore(root).Get);
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

    private static string HashInstalledProfile(string name) =>
        Convert.ToHexString(SHA256.HashData(InstalledProfileReader.ReadBytes(name)));

    private static CalibrationRestoreReport Report(string reason, IReadOnlyList<BindingRestoreResult>? bindings = null,
        IReadOnlyList<InterruptedPreviewResult>? recoveries = null) =>
        new(1, bindings?.Any(b => b.Status == "restored") == true, reason, DateTimeOffset.UtcNow, bindings ?? [], recoveries ?? []);
}
