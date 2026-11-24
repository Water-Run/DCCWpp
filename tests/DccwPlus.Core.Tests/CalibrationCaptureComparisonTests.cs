using DccwPlus.Core.Calibration;
using Xunit;

namespace DccwPlus.Core.Tests;

public sealed class CalibrationCaptureComparisonTests
{
    private const string Id = "0123456789abcdef";
    private static readonly string Hash = new('A', 64);
    private static readonly string OtherHash = new('B', 64);
    private static readonly DisplayCalibrationCapture Sample = new(Id, "Display", true, "sdr", "edid:" + Hash,
        "profile.icc", "current-user", "current-user", "read", Hash, "logical_ramp_readable", Hash);

    [Fact]
    public void SameRecordedStateMatchesWithoutMakingAnOpticalClaim()
    {
        var same = Sample with { ProfileName = "PROFILE.ICC", ProfileSha256 = Hash.ToLowerInvariant() };
        var result = CalibrationCaptureComparison.Compare(Report(Sample), Report(same));
        Assert.Equal("matched", result.Status);
        Assert.Equal("matched", result.TopologyStatus);
        Assert.False(result.ColorAccuracyVerified);
        Assert.Equal("matched", Assert.Single(result.Displays).LogicalRampStatus);
    }

    [Fact]
    public void CurveAndProfileDifferencesAreReportedSeparately()
    {
        var result = CalibrationCaptureComparison.Compare(Report(Sample),
            Report(Sample with { ProfileSha256 = OtherHash }));
        Assert.Equal("changed", result.Status);
        var difference = Assert.Single(result.Displays);
        Assert.Equal("changed", difference.ProfileStatus);
        Assert.Equal("matched", difference.LogicalRampStatus);
    }

    [Fact]
    public void AnExplicitlyAbsentDefaultCanMatchButAnUnreadableCurveCannot()
    {
        var absent = Sample with { ProfileName = null, ProfileScope = null, ProfileQueryStatus = "not-found", ProfileSha256 = null };
        var unavailable = absent with { CurveStatus = "curve_read_failed", LogicalRampSha256 = null };
        var result = CalibrationCaptureComparison.Compare(Report(unavailable), Report(unavailable));
        Assert.Equal("inconclusive", result.Status);
        Assert.Equal("matched", Assert.Single(result.Displays).ProfileStatus);
        Assert.Equal("inconclusive", Assert.Single(result.Displays).LogicalRampStatus);

        var scopeChanged = CalibrationCaptureComparison.Compare(Report(absent), Report(absent with { SelectedProfileScope = "system-wide" }));
        Assert.Equal("changed", Assert.Single(scopeChanged.Displays).ProfileStatus);
    }

    [Fact]
    public void MissingSamplesAndLegacyTopologyDoNotProveACompleteMatch()
    {
        var original = Report(Sample);
        var legacy = original with { TopologySha256 = null };
        var result = CalibrationCaptureComparison.Compare(legacy, legacy);
        Assert.Equal("inconclusive", result.TopologyStatus);
        Assert.Equal("matched", Assert.Single(result.Displays).Status);
        Assert.Equal("inconclusive", result.Status);

        result = CalibrationCaptureComparison.Compare(original, original with { Displays = [] });
        Assert.Equal("before-only", Assert.Single(result.Displays).Status);
        Assert.Equal("inconclusive", result.Status);
    }

    [Fact]
    public void NewDisplayPathOrColorModeIsNeverReportedAsARestoredCurve()
    {
        var original = Report(Sample);
        var swapped = Report(Sample with { PersistenceKey = "edid:" + OtherHash });
        var result = CalibrationCaptureComparison.Compare(original, swapped);
        Assert.Equal("identity-changed", Assert.Single(result.Displays).Status);
        Assert.Null(Assert.Single(result.Displays).LogicalRampStatus);

        var hdr = Report(Sample with { ColorState = "hdr", CurveStatus = "sdr_path_blocked", LogicalRampSha256 = null });
        result = CalibrationCaptureComparison.Compare(original, hdr with { TopologySha256 = OtherHash });
        Assert.Equal("changed", result.TopologyStatus);
        Assert.Null(Assert.Single(result.Displays).ProfileStatus);
    }

    [Fact]
    public void LocalAndRemoteEvidenceRemainInconclusiveEvenWhenAPathIdMatches()
    {
        var local = Report(Sample) with { IsRemoteSession = false };
        var remote = Report(Sample with { CurveStatus = "remote_session_blocked", LogicalRampSha256 = null,
            ProfileSha256 = OtherHash }) with { TopologySha256 = OtherHash, IsRemoteSession = true };

        var result = CalibrationCaptureComparison.Compare(local, remote);
        Assert.False(result.SameSessionType);
        Assert.Equal("inconclusive", result.TopologyStatus);
        Assert.Equal("inconclusive", result.Status);
        Assert.Null(Assert.Single(result.Displays).ProfileStatus);

        var same = CalibrationCaptureComparison.Compare(remote, remote);
        Assert.True(same.SameSessionType);
        Assert.Equal("inconclusive", same.Status);
        Assert.Equal("matched", Assert.Single(same.Displays).ProfileStatus);
        Assert.Throws<InvalidDataException>(() => CalibrationCaptureComparison.Compare(local, local with { IsRemoteSession = true }));
    }

    private static CalibrationCaptureReport Report(DisplayCalibrationCapture sample) =>
        new(1, new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero), 26100, "x64", "extend", [sample], Hash);
}
