using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;
using DccwPlus.Core.Storage;
using static DccwPlus.Windows.WindowsColorProfileQuery;

namespace DccwPlus.Windows;

public sealed partial class WindowsGammaCalibrationBackend
{
    public CalibrationUndoPlan PlanUndo(Guid calibrationId)
    {
        var saved = _store.Load(calibrationId);
        string displayId = saved.Original.DisplayId;
        string reason;
        try
        {
            CalibrationUndoPolicy.VerifySource(saved);
            using var gate = DisplayMutationLock.Acquire();
            _recovery.RequireNoInterruptedPreview();
            displayId = CheckUndo(saved, saved.Candidate).Current.DisplayId;
            reason = "undo_ready";
        }
        catch (ProfilePreviewException ex) { reason = ex.Code; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            reason = "undo_backup_unavailable";
        }
        return new(calibrationId, displayId, saved.InstalledProfile,
            saved.Original.ProfileName, reason == "undo_ready", reason);
    }

    internal GammaDeviceState CheckUndoStart(GammaCalibrationSession saved) => CheckUndo(saved, saved.Candidate).Current;

    private (GammaDeviceState Current, IccAssociationBackup Backup) CheckUndo(GammaCalibrationSession saved, GammaRamp expectedRamp)
    {
        CalibrationUndoPolicy.VerifySource(saved);
        WindowsDisplaySession.RequireLocal();
        var connected = new WindowsDisplayService().Enumerate();
        var (display, backup) = ResolveUndoAssociation(saved.Original, ReadAssociationBackup(saved), connected);
        var current = ReadSnapshot(display, connected);
        CalibrationUndoPolicy.VerifyCurrent(saved, current, expectedRamp);
        if (!ProfilePreviewPolicy.EqualName(backup.EffectiveDefault, saved.Original.ProfileName)
            || !ProfilePreviewPolicy.EqualName(backup.CandidateName, saved.InstalledProfile))
            throw new InvalidDataException("The saved ICC association does not match the calibration.");
        VerifyProfileHash(backup.CandidateName, backup.CandidateHash);
        VerifyProfileHash(backup.EffectiveDefault, backup.EffectiveDefaultHash);
        VerifyProfileHash(backup.UserDefault, backup.UserDefaultHash);
        if (!PreviousSystemDefaultMatches(backup, backup.EffectiveDefault,
            () => ReadDefault(Adapter(backup.TargetKey), backup.SourceId, SystemWide)))
            throw new ProfilePreviewException("undo_previous_profile_changed", "The previous system profile is no longer selected.");
        return (current, backup);
    }

    internal static (DisplayInfo Display, IccAssociationBackup Backup) ResolveUndoAssociation(GammaDeviceState saved,
        IccAssociationBackup backup, IReadOnlyList<DisplayInfo> connected)
    {
        var matches = connected.Where(display => MonitorIdentity.MatchesSavedDisplay(saved, display)).ToArray();
        if (matches.Length == 0)
            throw new ProfilePreviewException("display_disconnected", new LocalizationService()["DisplayUnavailable"]);
        if (matches.Length != 1)
            throw new ProfilePreviewException("undo_target_changed", new LocalizationService()["UndoUnavailable"]);

        var target = matches[0];
        if (!CalibrationService.IsExplicitSdr(target) || target.Topology == "clone"
            || connected.Count(display => display.DeviceName == target.DeviceName) != 1 || target.Profile?.Mhc2Present == true
            || target.TargetKey is null || target.SourceId is null
            || !string.Equals(target.ProfileDeviceKey, backup.DeviceKey, StringComparison.OrdinalIgnoreCase)
            || connected.Count(display => string.Equals(display.ProfileDeviceKey, backup.DeviceKey, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new ProfilePreviewException("undo_target_changed", new LocalizationService()["UndoUnavailable"]);

        // WCS uses the current adapter/source pair. The saved scope belongs to the original profile device key.
        return (target, backup with { TargetKey = target.TargetKey, SourceId = target.SourceId.Value });
    }

    private GammaProfileSelection CommitUndo(GammaCalibrationSession undo, Guid calibrationId)
    {
        var saved = _store.Load(calibrationId);
        if (!undo.Candidate.Matches(saved.Original.Ramp))
            throw new InvalidDataException("The undo curve does not match the saved calibration.");
        var (current, backup) = CheckUndo(saved, undo.Candidate);
        if (!GammaCalibrationTransaction.SameTarget(undo.Original, current)
            || !GammaCalibrationTransaction.SameProfile(undo.Original, current))
            throw new ProfilePreviewException("display_state_changed", "The display changed during the undo preview.");
        RestoreSavedAssociation(undo.Original, backup, current, saved.Original.ProfileName, undo.Id);

        var selection = new GammaProfileSelection(saved.Original.ProfileName, saved.Original.ProfileScope, ScopeName(backup.Scope));
        var restored = Read(undo.Original.DisplayId);
        if (!GammaCalibrationTransaction.SameTarget(undo.Original, restored) || !selection.Matches(restored))
            throw new ProfilePreviewException("undo_profile_not_verified", "Windows did not report the previous profile and scope.");
        if (NeedsCurveWriteAfterAssociation(undo, restored.Ramp, "undo_curve_changed")) TryWrite(restored, undo.Candidate);
        return selection;
    }

    private bool RestoreUndoProfile(GammaCalibrationSession undo, Guid calibrationId, GammaDeviceState expected, GammaDeviceState current,
        Action? beforeSelectionRestore)
    {
        var saved = _store.Load(calibrationId);
        CalibrationUndoPolicy.VerifySource(saved);
        var (_, backup) = ResolveUndoAssociation(saved.Original, ReadAssociationBackup(saved), new WindowsDisplayService().Enumerate());
        VerifyAssociationTarget(expected, backup, current);
        if (!undo.Candidate.Matches(saved.Original.Ramp)
            || !ProfilePreviewPolicy.EqualName(undo.Original.ProfileName, backup.CandidateName)
            || undo.Original.ProfileScope != "current-user")
            throw new InvalidDataException("The undo recovery record does not match the saved calibration.");
        VerifyProfileHash(backup.CandidateName, backup.CandidateHash);

        var adapter = Adapter(backup.TargetKey);
        string? userDefault = ReadDefault(adapter, backup.SourceId, 1);
        int scope = ReadScope(adapter, backup.SourceId);
        if (GammaCalibrationTransaction.SameProfile(undo.Original, current) && scope == 1
            && ProfilePreviewPolicy.EqualName(userDefault, backup.CandidateName)) return false;

        // The undo can have selected the former per-user default before restoring the former scope.
        bool expectedIntermediate = scope == 1 && (ProfilePreviewPolicy.EqualName(current.ProfileName, backup.UserDefault)
            || (backup.UserDefault is null && ProfilePreviewPolicy.EqualName(current.ProfileName, backup.EffectiveDefault)));
        if ((!GammaCalibrationTransaction.SameProfile(saved.Original, current) && !expectedIntermediate)
            || (!ProfilePreviewPolicy.EqualName(userDefault, backup.UserDefault)
                && !ProfilePreviewPolicy.EqualName(userDefault, backup.EffectiveDefault)
                && !ProfilePreviewPolicy.EqualName(userDefault, backup.CandidateName)))
            throw new ProfilePreviewException("external_profile_change", "Another application selected a different profile during undo.");

        VerifyProfileHash(backup.EffectiveDefault, backup.EffectiveDefaultHash);
        VerifyProfileHash(backup.UserDefault, backup.UserDefaultHash);
        bool alreadyAssociated = WindowsProfileAssociationBackend.List(backup.TargetKey, backup.SourceId, 1)
            .Contains(backup.CandidateName, StringComparer.OrdinalIgnoreCase);
        beforeSelectionRestore?.Invoke();
        VerifyAssociationWriteState(current, Read(current.DisplayId), scope, ReadScope(adapter, backup.SourceId),
            userDefault, ReadDefault(adapter, backup.SourceId, CurrentUser));
        if (alreadyAssociated)
            Check(ColorProfileSetDisplayDefaultAssociation(1, backup.CandidateName, 0, 4, adapter, backup.SourceId));
        else
            Check(ColorProfileAddDisplayAssociation(1, backup.CandidateName, adapter, backup.SourceId, true, false));
        if (scope == 0) SetUserScope(backup.DeviceKey, true);
        return true;
    }

    private static void VerifyProfileHash(string? profileName, string? hash)
    {
        if (profileName is null) return;
        if (!ProfilePreviewPolicy.IsHash(hash))
            throw new ProfilePreviewException("undo_backup_unavailable", "The calibration has no saved profile fingerprint.");
        if (!string.Equals(InstalledProfileReader.ReadSha256(profileName), hash, StringComparison.OrdinalIgnoreCase))
            throw new ProfilePreviewException("undo_profile_file_changed", "An ICC profile file changed since the calibration was saved.");
    }
}
