using DccwPlus.Core.Presets;

namespace DccwPlus.Core.Calibration;

// IdentityScope is always topology-query. TargetKey identifies the active path, not a monitor moved to another port.
public sealed record DisplayInfo(
    string Id,
    string Name,
    string DeviceName,
    bool IsPrimary,
    string ColorState,
    string IdentityScope = "topology-query",
    string Topology = "unknown",
    string? TargetKey = null,
    string? Output = null,
    int? Width = null,
    int? Height = null,
    int? PositionX = null,
    int? PositionY = null,
    int? RefreshMillihertz = null,
    int? CloneGroup = null,
    string? ProfileName = null,
    string? ProfileScope = null,
    bool? HdrActive = null,
    bool? AdvancedColorActive = null,
    ProfileReport? Profile = null,
    uint? SourceId = null,
    string? ProfileDeviceKey = null,
    string? PersistenceKey = null,
    string? PersistenceKind = null);

public interface IDisplayService { IReadOnlyList<DisplayInfo> Enumerate(); }

public sealed record CalibrationPlan(Guid PresetId, string DisplayId, string Backend, bool CanApply,
    string ReasonCode, IReadOnlyList<string> Steps);

// The same boundary is consumed by GUI, CLI, and future startup recovery.
// A successful plan is not evidence that a display has changed.
public sealed class CalibrationService(IDisplayService displays)
{
    public CalibrationPlan Plan(CalibrationPreset preset, string displayId)
    {
        PresetCodec.Validate(preset);
        var display = displays.Enumerate().FirstOrDefault(item => string.Equals(item.Id, displayId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException("The requested display is not connected.");
        if (!IsExplicitSdr(display))
            return new(preset.Id, displayId, "icc-wcs", false, "sdr_path_blocked",
            ["Read the display color mode", "Refuse the SDR calibration path while the mode is not explicit SDR", "Leave the display unchanged"]);
        return new(preset.Id, displayId, "icc-wcs", false, "calibration_backend_pending",
        ["Confirm the display is explicit SDR", "Snapshot the existing profile and vcgt presence",
         "Generate and validate an ICC calibration without replacing the saved association yet",
         "Preview with timed rollback", "Commit only after confirmation", "Verify and record the effective state"]);
    }

    // Explicit SDR means Windows reported SDR and both HDR and advanced color are inactive.
    public static bool IsExplicitSdr(DisplayInfo display) =>
        display.ColorState == "sdr" && display.HdrActive == false && display.AdvancedColorActive == false;
}

public sealed record RestorePlan(string DisplayId, bool CanRestore, string ReasonCode, IReadOnlyList<string> Steps);

// Compares a saved observation with the current read-only probe. It never writes an association or loads vcgt.
public static class RestorePlanner
{
    public static RestorePlan Plan(DisplayInfo current, DisplayObservation? savedObservation)
    {
        if (savedObservation is null)
            return Blocked(current.Id, "observation_not_found", "No saved display observation exists", "Leave the display unchanged");
        var saved = savedObservation.Displays.FirstOrDefault(item => string.Equals(item.Id, current.Id, StringComparison.Ordinal));
        if (saved is null)
            return Blocked(current.Id, "observation_missing_display", "The saved observation does not include this display", "Leave the display unchanged");
        if (!string.Equals(saved.Topology, current.Topology, StringComparison.Ordinal))
            return Blocked(current.Id, "topology_changed", "The saved topology does not match the current topology", "Leave the display unchanged");
        if (!CalibrationService.IsExplicitSdr(current))
            return Blocked(current.Id, "sdr_path_blocked", "Refuse restore while the display is not explicit SDR", "Leave the display unchanged");
        if (string.IsNullOrWhiteSpace(saved.ProfileName))
            return Blocked(current.Id, "profile_not_recorded", "The observation has no profile file name to restore", "Leave the display unchanged");
        if (string.Equals(saved.ProfileName, current.ProfileName, StringComparison.Ordinal)
            && string.Equals(saved.ProfileScope, current.ProfileScope, StringComparison.Ordinal))
            return Blocked(current.Id, "association_matches",
                "The current profile file name and scope match the observation",
                "A matching name is not a measured result",
                "Leave the display unchanged");
        return Blocked(current.Id, "restore_backend_pending",
            "Keep the saved profile file name and scope",
            "Reassociate that profile only after a confirmed preview exists",
            "Verify the association without treating a successful call as a measured result",
            "Do not load vcgt in this build");
    }

    private static RestorePlan Blocked(string displayId, string reason, params string[] steps) => new(displayId, false, reason, steps);
}
