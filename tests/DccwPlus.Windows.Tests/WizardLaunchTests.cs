using DccwPlus.Core.Calibration;
using DccwPlus.Windows;
using Xunit;

namespace DccwPlus.Windows.Tests;

public sealed class WizardLaunchTests
{
    private static readonly DisplayInfo Display = new("0123456789abcdef", "Fixture", @"\\.\DISPLAY1", true, "sdr",
        TargetKey: "00000000:00000001:1", SourceId: 1, PersistenceKey: "path:" + new string('A', 64),
        ProfileDeviceKey: @"\Registry\Machine\System\Fixture");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActivationRoundTripsOnlyAnIdentityAndPresetReference(bool hasSelection)
    {
        var request = new WizardLaunchRequest(Guid.NewGuid(), hasSelection ? Display.Id : null,
            hasSelection ? WizardLaunchRequest.Stamp(Display) : null, hasSelection ? Guid.NewGuid() : null);
        Assert.Equal(request, WizardLaunchRequest.Parse(request.Arguments()));
        Assert.DoesNotContain(Display.ProfileDeviceKey!, string.Join(' ', request.Arguments()));
    }

    [Theory]
    [InlineData(1, "00000000000000000000000000000000")]
    [InlineData(2, "..\\test")]
    [InlineData(2, "-")]
    [InlineData(3, "-")]
    [InlineData(3, "ABCDEF")]
    [InlineData(4, "00000000-0000-0000-0000-000000000000")]
    [InlineData(4, "--preview")]
    public void MalformedActivationIsNotTreatedAsAWizardRequest(int index, string invalid)
    {
        var request = new WizardLaunchRequest(Guid.NewGuid(), Display.Id, WizardLaunchRequest.Stamp(Display), Guid.NewGuid());
        string[] arguments = request.Arguments();
        arguments[index] = invalid;
        Assert.Null(WizardLaunchRequest.Parse(arguments));
    }

    [Fact]
    public void SelectionRejectsAReusedQueryIdAndFollowsUnchangedPathsAcrossCosmeticUpdates()
    {
        var request = new WizardLaunchRequest(Guid.NewGuid(), Display.Id, WizardLaunchRequest.Stamp(Display), null);
        Assert.False(request.Matches(Display with { SourceId = 2 }));
        Assert.False(request.Matches(Display with { ProfileDeviceKey = null }));
        Assert.False(request.Matches(Display with { PersistenceKey = "path:" + new string('B', 64) }));
        Assert.True(request.Matches(Display with { Name = "Another label", ColorState = "hdr", PositionX = 800 }));
    }
}
