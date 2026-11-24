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
    public void CaptureRejectsTopologyChangesEvenWhenRestrictedToOneDisplay()
    {
        var display = Display();
        int reads = 0;
        var capture = new CalibrationCapture(() => ++reads == 1 ? new("extend", [display]) : new("unknown", []),
            _ => GammaRamp.Identity(), _ => [1]);
        Assert.Equal("capture_state_changed", Assert.Throws<ProfilePreviewException>(() => capture.Read(display.Id)).Code);
    }

    private static DisplayInfo Display() => new("0123456789abcdef", "Panel", "SDR", true, "sdr",
        Topology: "extend", TargetKey: "00000000:00000001:1", ProfileName: "panel.icc", ProfileScope: "system-wide",
        HdrActive: false, AdvancedColorActive: false, SourceId: 1, SelectedProfileScope: "system-wide", ProfileQueryStatus: "read");
}
