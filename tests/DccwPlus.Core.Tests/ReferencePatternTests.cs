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
    [InlineData(900, 270, 150, 135, 300, 0)]
    [InlineData(270, 900, 135, 150, 0, 300)]
    public void LiveGammaPlacesThreeLargeDotsAcrossTheLongDimension(int width, int height,
        int x, int y, int horizontalStep, int verticalStep)
    {
        var image = ReferencePattern.Draw(ReferencePatternKind.Gamma, width, height);
        for (int index = 0; index < 3; index++)
            Assert.Equal(186, Pixel(image, width, x + index * horizontalStep, y + index * verticalStep)[0]);
        Assert.Equal(255, Pixel(image, width, x + 60, y)[0]);
        Assert.Equal(0, Pixel(image, width, x + 61, y)[0]);
    }

    [Theory]
    [InlineData(ReferencePatternKind.Brightness, new byte[] { 1, 2, 4, 8, 12, 16, 24, 32 }, 0)]
    [InlineData(ReferencePatternKind.Contrast, new byte[] { 223, 231, 239, 243, 247, 251, 253, 254 }, 255)]
    public void HardwarePatternsSeparateEveryPatchFromTheReferenceBackground(
        ReferencePatternKind kind, byte[] expected, byte background)
    {
        const int width = 160, height = 80;
        var image = ReferencePattern.Draw(kind, width, height);
        for (int index = 0; index < expected.Length; index++)
        {
            byte level = expected[index];
            Assert.Equal(new byte[] { level, level, level, 255 }, Pixel(image, width, index * 20 + 10, height / 2));
            Assert.Equal(background, Pixel(image, width, index * 20, height / 2)[0]);
        }
        Assert.Equal(background, Pixel(image, width, 40, 1)[0]);
    }

    private static byte[] Pixel(byte[] image, int width, int x, int y) => image.AsSpan((y * width + x) * 4, 4).ToArray();
}
