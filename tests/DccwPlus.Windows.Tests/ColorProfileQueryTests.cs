using DccwPlus.Core.Calibration;
using DccwPlus.Windows;
using Xunit;
using static DccwPlus.Windows.WindowsColorProfileQuery;

namespace DccwPlus.Windows.Tests;

public sealed class ColorProfileQueryTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070002))]
    [InlineData(unchecked((int)0x80070490))]
    public void WindowsMissingDefaultRemainsAnEmptyAssociation(int result)
    {
        Assert.Null(DecodeDefault(result, null));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), null)]
    [InlineData(0, null)]
    [InlineData(0, "  ")]
    public void FailedOrInvalidReadsCannotSupplyARollbackBaseline(int result, string? name)
    {
        var error = Assert.Throws<ProfilePreviewException>(() => DecodeDefault(result, name));
        Assert.Equal("profile_state_unavailable", error.Code);
    }

    [Fact]
    public void UserScopeCanInheritTheSystemDefault()
    {
        var profile = ReadEffective(CurrentUser, scope => scope == CurrentUser ? null : "panel.icc");
        Assert.Equal("panel.icc", profile.Name);
        Assert.Equal("system-wide", profile.Scope);
        Assert.Equal("current-user", profile.SelectedScope);
        Assert.Equal("read", profile.Status);
    }

    [Fact]
    public void UserQueryFailureDoesNotFallBackToSystemDefault()
    {
        var error = Assert.Throws<ProfilePreviewException>(() => ReadEffective(CurrentUser, scope =>
        {
            Assert.Equal(CurrentUser, scope);
            return DecodeDefault(unchecked((int)0x80070005), null);
        }));
        Assert.Equal("profile_state_unavailable", error.Code);
    }
}
