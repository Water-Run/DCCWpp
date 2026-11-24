using System.Security.Cryptography;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Storage;

namespace DccwPlus.Windows;

public sealed partial class WindowsGammaCalibrationBackend
{
    public CalibrationUndoPlan PlanUndo(Guid calibrationId)
    {
        var saved = _store.Load(calibrationId);
        string reason;
        try
        {
            _ = CheckUndo(saved, saved.Candidate);
            reason = "undo_ready";
        }
        catch (ProfilePreviewException ex) { reason = ex.Code; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            reason = "undo_backup_unavailable";
        }
        return new(calibrationId, saved.Original.DisplayId, saved.InstalledProfile,
            saved.Original.ProfileName, reason == "undo_ready", reason);
    }

    internal GammaDeviceState CheckUndoStart(GammaCalibrationSession saved) => CheckUndo(saved, saved.Candidate).Current;

    private (GammaDeviceState Current, IccAssociationBackup Backup) CheckUndo(GammaCalibrationSession saved, GammaRamp expectedRamp)
    {
        CalibrationUndoPolicy.VerifySource(saved);
        var current = Read(saved.Original.DisplayId);
        CalibrationUndoPolicy.VerifyCurrent(saved, current, expectedRamp);
        var backup = ReadAssociationBackup(saved);
        VerifyAssociationTarget(saved, backup, current);
        if (!ProfilePreviewPolicy.EqualName(backup.EffectiveDefault, saved.Original.ProfileName)
            || !ProfilePreviewPolicy.EqualName(backup.CandidateName, saved.InstalledProfile))
            throw new InvalidDataException("The saved ICC association does not match the calibration.");
        VerifyProfileHash(backup.CandidateName, backup.CandidateHash);
        VerifyProfileHash(backup.EffectiveDefault, backup.EffectiveDefaultHash);
        VerifyProfileHash(backup.UserDefault, backup.UserDefaultHash);
        if (backup.Scope == 0 || backup.UserDefault is null)
        {
            var system = ReadDefault(Adapter(backup.TargetKey), backup.SourceId, 0);
            if (!ProfilePreviewPolicy.EqualName(system, backup.EffectiveDefault))
                throw new ProfilePreviewException("undo_previous_profile_changed", "The previous system profile is no longer selected.");
        }
        return (current, backup);
    }

    private string? CommitUndo(GammaCalibrationSession undo, Guid calibrationId)
    {
        var saved = _store.Load(calibrationId);
        if (!undo.Candidate.Matches(saved.Original.Ramp))
            throw new InvalidDataException("The undo curve does not match the saved calibration.");
        var (current, backup) = CheckUndo(saved, undo.Candidate);
        if (!GammaCalibrationTransaction.SameTarget(undo.Original, current)
            || !GammaCalibrationTransaction.SameProfile(undo.Original, current))
            throw new ProfilePreviewException("display_state_changed", "The display changed during the undo preview.");
        AtomicFile.Write(Path.Combine(_store.DirectoryFor(undo.Id), "association-mutation-started"), []);
        RestoreSavedAssociation(saved, backup, current);

        var restored = Read(undo.Original.DisplayId);
        if (!GammaCalibrationTransaction.SameTarget(undo.Original, restored)
            || !GammaCalibrationTransaction.SameProfile(saved.Original, restored))
            throw new ProfilePreviewException("undo_profile_not_verified", "Windows did not report the previous profile and scope.");
        if (!restored.Ramp.Matches(undo.Candidate, 257))
        {
            if (!restored.Ramp.Matches(undo.Original.Ramp, 257) && !restored.Ramp.Matches(GammaRamp.Identity(), 257))
                throw new ProfilePreviewException("undo_curve_changed", "The display curve changed during the profile switch.");
            Write(restored, undo.Candidate);
        }
        return restored.ProfileName;
    }

    private void RestoreUndoProfile(GammaCalibrationSession undo, Guid calibrationId, GammaDeviceState current)
    {
        var saved = _store.Load(calibrationId);
        CalibrationUndoPolicy.VerifySource(saved);
        var backup = ReadAssociationBackup(saved);
        VerifyAssociationTarget(undo, backup, current);
        if (!undo.Candidate.Matches(saved.Original.Ramp)
            || !ProfilePreviewPolicy.EqualName(undo.Original.ProfileName, backup.CandidateName)
            || undo.Original.ProfileScope != "current-user")
            throw new InvalidDataException("The undo recovery record does not match the saved calibration.");
        VerifyProfileHash(backup.CandidateName, backup.CandidateHash);

        var adapter = Adapter(backup.TargetKey);
        string? userDefault = ReadDefault(adapter, backup.SourceId, 1);
        Check(ColorProfileGetDisplayUserScope(adapter, backup.SourceId, out int scope));
        if (scope is not (0 or 1)) throw new InvalidDataException("Unknown current color profile scope.");
        if (GammaCalibrationTransaction.SameProfile(undo.Original, current) && scope == 1
            && ProfilePreviewPolicy.EqualName(userDefault, backup.CandidateName)) return;

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
        if (WindowsProfileAssociationBackend.List(backup.TargetKey, backup.SourceId, 1)
            .Contains(backup.CandidateName, StringComparer.OrdinalIgnoreCase))
            Check(ColorProfileSetDisplayDefaultAssociation(1, backup.CandidateName, 0, 4, adapter, backup.SourceId));
        else
            Check(ColorProfileAddDisplayAssociation(1, backup.CandidateName, adapter, backup.SourceId, true, false));
        if (scope == 0) SetUserScope(backup.DeviceKey, true);
    }

    private static string HashInstalled(string profileName) =>
        Convert.ToHexString(SHA256.HashData(InstalledProfileReader.ReadBytes(profileName)));

    private static void VerifyProfileHash(string? profileName, string? hash)
    {
        if (profileName is null) return;
        if (!ProfilePreviewPolicy.IsHash(hash))
            throw new ProfilePreviewException("undo_backup_unavailable", "The calibration has no saved profile fingerprint.");
        if (!string.Equals(HashInstalled(profileName), hash, StringComparison.OrdinalIgnoreCase))
            throw new ProfilePreviewException("undo_profile_file_changed", "An ICC profile file changed since the calibration was saved.");
    }
}
