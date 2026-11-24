using DccwPlus.Core.Calibration;
using DccwPlus.Windows;
using Xunit;

namespace DccwPlus.Windows.Tests;

public sealed class DisplayColorProbeTests
{
    [Theory]
    [InlineData(0u, false, "sdr", false, false, 4)]
    [InlineData(0u, true, "advanced-color", null, true, null)]
    [InlineData(1u, true, "wcg", false, true, 4)]
    [InlineData(2u, true, "hdr", true, true, 8)]
    [InlineData(2u, false, "hdr", true, true, 8)]
    [InlineData(3u, false, "unknown", null, null, null)]
    [InlineData(3u, true, "advanced-color", null, true, null)]
    public void DetailedModeSelectsTheCorrespondingProfileAndSdrGate(
        uint mode, bool active, string expectedState, bool? hdr, bool? advanced, int? profileSubtype)
    {
        var color = DisplayConfigurationQuery.DescribeColor(mode, active);
        Assert.Equal(expectedState, color.State);
        Assert.Equal(hdr, color.HdrActive);
        Assert.Equal(advanced, color.AdvancedColorActive);
        Assert.Equal(profileSubtype, color.ProfileSubtype);
        var display = new DisplayInfo("display", "Panel", "DISPLAY", true, color.State,
            HdrActive: color.HdrActive, AdvancedColorActive: color.AdvancedColorActive);
        Assert.Equal(expectedState == "sdr", CalibrationService.IsExplicitSdr(display));
    }
}
