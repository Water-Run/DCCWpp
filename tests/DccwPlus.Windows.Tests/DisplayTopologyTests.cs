using Xunit;
using static DccwPlus.Windows.DisplayConfigurationQuery;

namespace DccwPlus.Windows.Tests;

public sealed class DisplayTopologyTests
{
    [Theory]
    [InlineData(0x1u, 0x00000001u, null)]
    [InlineData(0x9u, 0x00010003u, 3)]
    [InlineData(0x9u, 0x0001ffffu, null)]
    public void DecodesNativeAndVirtualSourceModeIndices(uint flags, uint index, int? cloneGroup)
    {
        var path = Path(flags, index);
        var mode = SourceMode(path, [new() { InfoType = 2 }, Mode()]);
        Assert.Equal(1920u, mode?.Width);
        Assert.Equal(-1920, mode?.PositionX);
        Assert.Equal(cloneGroup, CloneGroup(path));
    }

    [Theory]
    [InlineData(0x1u, uint.MaxValue)]
    [InlineData(0x9u, 0xffff0003u)]
    public void IgnoresAnUnreportedOrMismatchedSourceMode(uint flags, uint index)
    {
        Assert.Null(SourceMode(Path(flags, index), [Mode()]));
        var wrongAdapter = Mode();
        wrongAdapter.AdapterHigh++;
        Assert.Null(SourceMode(Path(0x1u, 0u), [wrongAdapter]));
    }

    [Fact]
    public void PollingDetectsColorAndLayoutChangesOnTheSameDisplayPath()
    {
        const string monitor = "fixture-monitor";
        var path = Path(0x1u, 0u);
        var original = Mode();
        var sdr = DescribeColor(0u, false);
        string before = PathStamp(path, [original], monitor, sdr);

        Assert.NotEqual(before, PathStamp(path, [original], monitor, DescribeColor(2u, true)));
        var after = original;
        after.Width = 1280;
        after.PositionX = -1280;
        Assert.NotEqual(before, PathStamp(path, [after], monitor, sdr));

        path.SourceModeInfo = 1u;
        Assert.Equal(before, PathStamp(path, [new() { InfoType = 2 }, original], monitor, sdr));
    }

    private static PathInfo Path(uint flags, uint sourceMode) => new()
    {
        Flags = flags, SourceModeInfo = sourceMode, SourceAdapterLow = 2, SourceId = 7,
        TargetAdapterLow = 3, TargetId = 5, TargetAvailable = true
    };

    private static ModeInfo Mode() => new()
    {
        InfoType = 1, AdapterLow = 2, Id = 7, Width = 1920, Height = 1080, PositionX = -1920
    };
}
