using DccwPlus.Core.Calibration;
using Xunit;

namespace DccwPlus.Core.Tests;

public sealed class ReferencePatternTests
{
    [Theory]
    [InlineData(GammaExample.TooLow, 217)]
    [InlineData(GammaExample.Balanced, 186)]
    [InlineData(GammaExample.TooHigh, 151)]
    public void GammaExamplesChangeTheDotAndKeepASinglePixelCheckerboard(GammaExample example, int center)
    {
        const int width = 200, height = 200;
        var image = ReferencePattern.Draw(ReferencePatternKind.Gamma, width, height, example);
        Assert.Equal(new byte[] { (byte)center, (byte)center, (byte)center, 255 }, Pixel(image, width, 100, 100));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(image, width, 150, 100));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(image, width, 151, 100));
    }

    [Theory]
    [InlineData(ReferencePatternKind.Brightness, 0, 28, 0)]
    [InlineData(ReferencePatternKind.Contrast, 227, 255, 255)]
    public void HardwarePatternsRetainTheDarkAndLightLevelsAgainstTheirOwnBackground(
        ReferencePatternKind kind, byte first, byte last, byte background)
    {
        const int width = 80, height = 40;
        var image = ReferencePattern.Draw(kind, width, height);
        Assert.Equal(first, Pixel(image, width, 5, height / 2)[0]);
        Assert.Equal(last, Pixel(image, width, 75, height / 2)[0]);
        Assert.Equal(background, Pixel(image, width, 40, 1)[0]);
    }

    private static byte[] Pixel(byte[] image, int width, int x, int y) => image.AsSpan((y * width + x) * 4, 4).ToArray();
}
