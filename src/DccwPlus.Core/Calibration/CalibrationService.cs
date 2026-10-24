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
    bool? AdvancedColorActive = null);

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
        if (!displays.Enumerate().Any(d => string.Equals(d.Id, displayId, StringComparison.Ordinal)))
            throw new KeyNotFoundException("The requested display is not connected.");
        return new(preset.Id, displayId, "icc-wcs", false, "calibration_backend_pending",
        ["Detect SDR/HDR and display identity", "Snapshot existing profile and calibration",
         "Generate and validate ICC calibration", "Preview with timed rollback",
         "Commit only after confirmation", "Verify and record effective state"]);
    }
}
