namespace DccwPlus.Core.Calibration;

public sealed record CalibrationUndoPlan(Guid CalibrationId, string DisplayId, string? CurrentProfile,
    string? PreviousProfile, bool CanPreview, string ReasonCode);

public static class CalibrationUndoPolicy
{
    public static void VerifySource(GammaCalibrationSession saved)
    {
        if (saved.Status != "confirmed" || saved.IsBindingRestore || saved.UndoOf is not null || saved.InstalledProfile is null)
            throw new ProfilePreviewException("undo_not_confirmed", "Choose a confirmed DCCW++ calibration.");
    }

    public static void VerifyCurrent(GammaCalibrationSession saved, GammaDeviceState now, GammaRamp expectedRamp)
    {
        VerifySource(saved);
        if (!now.ExplicitSdr || now.SharedSource || !GammaCalibrationTransaction.SameRecoveryTarget(saved.Original, now))
            throw new ProfilePreviewException("undo_target_changed", "The original independent SDR display is unavailable.");
        if (now.ProfileScope != "current-user" || !ProfilePreviewPolicy.EqualName(now.ProfileName, saved.InstalledProfile))
            throw new ProfilePreviewException("undo_profile_changed", "That calibration is no longer selected for this display.");
        if (!now.Ramp.Matches(expectedRamp, 257))
            throw new ProfilePreviewException("undo_curve_changed", "The display curve changed since this calibration was confirmed.");
    }
}
