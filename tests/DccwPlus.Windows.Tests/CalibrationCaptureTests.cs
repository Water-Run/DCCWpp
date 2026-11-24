using DccwPlus.Core.Calibration;
using DccwPlus.Windows;
using Xunit;

namespace DccwPlus.Windows.Tests;

public sealed class CalibrationCaptureTests
{
    [Fact]
    public void MultiDisplayCaptureKeepsAvailableEvidenceForBothModes()
    {
        var sdr = Display();
        var hdr = sdr with { Id = "fedcba9876543210", DeviceName = "HDR", ColorState = "hdr",
            HdrActive = true, AdvancedColorActive = true, ProfileName = "hdr.icc" };
        var read = new CalibrationCapture(() => new("extend", [hdr, sdr]), device =>
        {
            Assert.Equal(sdr.DeviceName, device);
            return GammaRamp.Identity();
        }, profile => profile == "hdr.icc" ? [1, 2, 3] : [4, 5, 6]);

        var captures = read.Read().Displays;
        Assert.Collection(captures, sample =>
        {
            Assert.Equal(sdr.Id, sample.DisplayId);
            Assert.Equal("logical_ramp_readable", sample.CurveStatus);
            Assert.Matches("^[0-9A-F]{64}$", sample.LogicalRampSha256!);
            Assert.Matches("^[0-9A-F]{64}$", sample.ProfileSha256!);
        }, sample =>
        {
            Assert.Equal(hdr.Id, sample.DisplayId);
            Assert.Equal("sdr_path_blocked", sample.CurveStatus);
            Assert.Null(sample.LogicalRampSha256);
            Assert.Matches("^[0-9A-F]{64}$", sample.ProfileSha256!);
        });
        Assert.Equal(captures[1], Assert.Single(read.Read(hdr.Id).Displays));
    }

    [Fact]
    public void CaptureRejectsAChangingLogicalCurve()
    {
        var display = Display();
        int reads = 0;
        var capture = new CalibrationCapture(() => new("extend", [display]),
            _ => ++reads == 1 ? GammaRamp.Identity() : GammaRampMath.Compose(GammaRamp.Identity(), new() { RedGain = .8 }),
            _ => [1]);
        Assert.Equal("capture_state_changed", Assert.Throws<ProfilePreviewException>(() => capture.Read()).Code);
    }

    [Fact]
    public void RemoteCaptureKeepsProfileEvidenceWithoutReadingTheLogicalCurve()
    {
        var capture = new CalibrationCapture(() => new("extend", [Display()]),
            _ => throw new InvalidOperationException("Remote captures must not read a gamma ramp."),
            _ => [1, 2, 3], () => true);

        var report = capture.Read();
        var sample = Assert.Single(report.Displays);
        Assert.True(report.IsRemoteSession);
        Assert.Equal("remote_session_blocked", sample.CurveStatus);
        Assert.Null(sample.LogicalRampSha256);
        Assert.NotNull(sample.ProfileSha256);
    }

    [Fact]
    public void CaptureRejectsAChangedSessionTypeEvenWhenTheDisplayListMatches()
    {
        int reads = 0;
        var capture = new CalibrationCapture(() => new("extend", [Display()]), _ => GammaRamp.Identity(),
            _ => [1], () => reads++ == 0);

        Assert.Equal("capture_state_changed", Assert.Throws<ProfilePreviewException>(() => capture.Read()).Code);
    }

    [Fact]
    public void CaptureRejectsTopologyChangesEvenWhenRestrictedToOneDisplay()
    {
        var display = Display();
        int reads = 0;
        var capture = new CalibrationCapture(() => ++reads == 1 ? new("extend", [display]) : new("unknown", []),
            _ => GammaRamp.Identity(), _ => [1]);
        Assert.Equal("capture_state_changed", Assert.Throws<ProfilePreviewException>(() => capture.Read(display.Id)).Code);
    }

    [Fact]
    public void TopologyHashCoversAllPathsEvenWithOneDisplaySampled()
    {
        var display = Display() with { Width = 1920, Height = 1080, PositionX = 0, PositionY = 0, RefreshMillihertz = 60_000 };
        var secondary = display with { Id = "fedcba9876543210", TargetKey = "00000000:00000001:2", SourceId = 2,
            DeviceName = "Secondary", PositionX = -1920, IsPrimary = false };
        DisplayProbe probe = new("extend", [secondary, display]);
        var capture = new CalibrationCapture(() => probe, _ => GammaRamp.Identity(), _ => [1]);

        var first = capture.Read(display.Id);
        Assert.Matches("^[0-9A-F]{64}$", first.TopologySha256!);
        probe = new("extend", [display, secondary with { ProfileName = "other.icc" }]);
        Assert.Equal(first.TopologySha256, capture.Read().TopologySha256);
        probe = new("extend", [display, secondary with { PositionX = 1920 }]);
        Assert.NotEqual(first.TopologySha256, capture.Read(display.Id).TopologySha256);
        probe = new("extend", [display, secondary with { RefreshMillihertz = 120_000 }]);
        Assert.NotEqual(first.TopologySha256, capture.Read(display.Id).TopologySha256);
    }

    private static DisplayInfo Display() => new("0123456789abcdef", "Panel", "SDR", true, "sdr",
        Topology: "extend", TargetKey: "00000000:00000001:1", ProfileName: "panel.icc", ProfileScope: "system-wide",
        HdrActive: false, AdvancedColorActive: false, SourceId: 1, SelectedProfileScope: "system-wide", ProfileQueryStatus: "read");
}
