namespace DccwPlus.Core.Calibration;

public sealed record DisplayCaptureDifference(string DisplayId, string Status, string? ProfileStatus, string? LogicalRampStatus,
    DisplayCalibrationCapture? Before, DisplayCalibrationCapture? After);

public sealed record CaptureComparisonResult(int SchemaVersion, string Status, string TopologyStatus,
    bool SameOsBuildAndArchitecture, DateTimeOffset BeforeCapturedAt, DateTimeOffset AfterCapturedAt,
    IReadOnlyList<DisplayCaptureDifference> Displays)
{
    public bool ColorAccuracyVerified => false;
}

public static class CalibrationCaptureComparison
{
    private const string Matched = "matched";
    private const string Changed = "changed";
    private const string Inconclusive = "inconclusive";

    public static CaptureComparisonResult Compare(CalibrationCaptureReport before, CalibrationCaptureReport after)
    {
        CalibrationCaptureCodec.Validate(before);
        CalibrationCaptureCodec.Validate(after);
        var oldDisplays = before.Displays.ToDictionary(display => display.DisplayId, StringComparer.Ordinal);
        var newDisplays = after.Displays.ToDictionary(display => display.DisplayId, StringComparer.Ordinal);
        var displays = oldDisplays.Keys.Union(newDisplays.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(id => CompareDisplay(id, oldDisplays.GetValueOrDefault(id), newDisplays.GetValueOrDefault(id))).ToArray();
        string topologyStatus = before.Topology == "unknown" || after.Topology == "unknown" ? Inconclusive
            : before.Topology != after.Topology ? Changed : CompareHashes(before.TopologySha256, after.TopologySha256);
        bool sameOs = before.WindowsBuild == after.WindowsBuild && before.OsArchitecture == after.OsArchitecture;
        string status = topologyStatus == Changed || displays.Any(display => display.Status is Changed or "identity-changed") ? Changed
            : topologyStatus == Matched && sameOs && displays.Length > 0 && displays.All(display => display.Status == Matched)
                ? Matched : Inconclusive;
        return new(1, status, topologyStatus, sameOs, before.CapturedAt, after.CapturedAt, displays);
    }

    private static DisplayCaptureDifference CompareDisplay(string id, DisplayCalibrationCapture? before, DisplayCalibrationCapture? after)
    {
        if (before is null) return new(id, "after-only", null, null, null, after);
        if (after is null) return new(id, "before-only", null, null, before, null);
        if (before.PersistenceKey is not null && after.PersistenceKey is not null
            && !string.Equals(before.PersistenceKey, after.PersistenceKey, StringComparison.OrdinalIgnoreCase))
            return new(id, "identity-changed", null, null, before, after);
        if (before.ColorState != after.ColorState) return new(id, Changed, null, null, before, after);

        string profile = CompareProfile(before, after);
        string curve = CompareHashes(before.LogicalRampSha256, after.LogicalRampSha256);
        string status = before.IsPrimary != after.IsPrimary || profile == Changed || curve == Changed ? Changed
            : profile == Matched && curve == Matched ? Matched : Inconclusive;
        return new(id, status, profile, curve, before, after);
    }

    private static string CompareProfile(DisplayCalibrationCapture before, DisplayCalibrationCapture after)
    {
        if (before.ProfileQueryStatus is not ("read" or "not-found") || after.ProfileQueryStatus is not ("read" or "not-found"))
            return Inconclusive;
        if (!ProfilePreviewPolicy.EqualName(before.ProfileName, after.ProfileName) || before.ProfileScope != after.ProfileScope
            || before.SelectedProfileScope != after.SelectedProfileScope) return Changed;
        return before.ProfileName is null ? Matched : CompareHashes(before.ProfileSha256, after.ProfileSha256);
    }

    private static string CompareHashes(string? before, string? after) => before is null || after is null ? Inconclusive
        : string.Equals(before, after, StringComparison.OrdinalIgnoreCase) ? Matched : Changed;
}
