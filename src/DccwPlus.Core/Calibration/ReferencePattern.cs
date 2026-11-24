namespace DccwPlus.Core.Calibration;

public enum ReferencePatternKind { Overview, Gamma, Brightness, Contrast, GrayBalance, Compare }
public enum GammaExample { Live, Balanced, TooLow, TooHigh }

public static class ReferencePattern
{
    public static byte[] Draw(ReferencePatternKind kind, int width, int height, GammaExample example = GammaExample.Live)
    {
        if (width is < 1 or > 4096 || height is < 1 or > 2160)
            throw new ArgumentOutOfRangeException(nameof(width), "The reference pattern must fit within 4096 by 2160 pixels.");
        var pixels = new byte[width * height * 4];
        int tiles = example == GammaExample.Live ? 3 : 1;
        double radius = Math.Min(width / (double)tiles, height / (double)tiles) * .35;
        double radiusSquared = radius * radius;
        byte center = example switch { GammaExample.TooLow => 217, GammaExample.TooHigh => 151, _ => 186 };
        for (int y = 0, offset = 0; y < height; y++)
        {
            double dy = y - (y * tiles / height + .5) * height / tiles;
            for (int x = 0; x < width; x++, offset += 4)
            {
                byte r, g, b;
                switch (kind)
                {
                    case ReferencePatternKind.Gamma:
                        double dx = x - (x * tiles / width + .5) * width / tiles;
                        double distance = dx * dx + dy * dy;
                        byte checker = (x + y) % 2 == 0 ? (byte)0 : (byte)255;
                        r = g = b = distance < radiusSquared * .22 ? center : distance < radiusSquared ? checker : (byte)128;
                        break;
                    case ReferencePatternKind.Brightness:
                        r = g = b = y * 5 < height || y * 5 >= height * 4 ? (byte)0 : (byte)(x * 8 / width * 4);
                        break;
                    case ReferencePatternKind.Contrast:
                        r = g = b = y * 5 < height || y * 5 >= height * 4 ? (byte)255 : (byte)(227 + x * 8 / width * 4);
                        break;
                    case ReferencePatternKind.GrayBalance:
                        r = g = b = (byte)(16 + x * 8 / width * 32);
                        break;
                    default:
                        r = g = b = width == 1 ? (byte)128 : (byte)(x * 255 / (width - 1));
                        if (y * 3 > height * 2)
                        {
                            int band = x * 6 / width;
                            r = band is 0 or 3 or 5 ? (byte)220 : (byte)32;
                            g = band is 1 or 3 or 4 ? (byte)220 : (byte)32;
                            b = band is 2 or 4 or 5 ? (byte)220 : (byte)32;
                        }
                        break;
                }
                pixels[offset] = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
                pixels[offset + 3] = 255;
            }
        }
        return pixels;
    }
}
