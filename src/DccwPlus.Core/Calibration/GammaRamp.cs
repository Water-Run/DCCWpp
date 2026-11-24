using DccwPlus.Core.Presets;

namespace DccwPlus.Core.Calibration;

public sealed record GammaRamp(ushort[] Red, ushort[] Green, ushort[] Blue)
{
    public static GammaRamp Identity() => new(Channel(), Channel(), Channel());
    private static ushort[] Channel() => Enumerable.Range(0, 256).Select(i => (ushort)(i * 257)).ToArray();
    public GammaRamp Copy() => new((ushort[])Red.Clone(), (ushort[])Green.Clone(), (ushort[])Blue.Clone());
    public void Validate()
    {
        foreach (var channel in new[] { Red, Green, Blue })
        {
            if (channel is not { Length: 256 }) throw new InvalidDataException("A gamma channel must contain 256 entries.");
            if (channel[255] - channel[0] < 8192) throw new InvalidDataException("The gamma channel has insufficient visible range.");
            for (int i = 1; i < channel.Length; i++)
                if (channel[i] < channel[i - 1]) throw new InvalidDataException("Gamma channels must be monotonic.");
        }
    }
    public bool Matches(GammaRamp other, int tolerance = 0) =>
        Near(Red, other.Red, tolerance) && Near(Green, other.Green, tolerance) && Near(Blue, other.Blue, tolerance);
    private static bool Near(ushort[] a, ushort[] b, int tolerance) => a.Length == b.Length
        && a.Zip(b).All(pair => Math.Abs((int)pair.First - pair.Second) <= tolerance);
}

public static class GammaRampMath
{
    // 2.2 is the neutral slider position. This is an adjustment over the captured ramp,
    // not a measurement of the display's physical gamma or a replacement ICC TRC.
    public static GammaRamp Compose(GammaRamp baseline, SdrCalibration settings)
    {
        PresetCodec.Validate(CalibrationPreset.Create("Calibration", settings));
        baseline.Validate();
        return new(ComposeChannel(baseline.Red, settings.Gamma, settings.RedGain),
            ComposeChannel(baseline.Green, settings.Gamma, settings.GreenGain),
            ComposeChannel(baseline.Blue, settings.Gamma, settings.BlueGain));
    }
    private static ushort[] ComposeChannel(ushort[] baseline, double gamma, double gain)
    {
        var output = new ushort[256];
        for (int i = 0; i < output.Length; i++)
        {
            double input = Math.Clamp(Math.Pow(i / 255d, 2.2 / gamma) * gain, 0, 1) * 255;
            int lower = (int)Math.Floor(input), upper = Math.Min(lower + 1, 255);
            output[i] = (ushort)Math.Round(baseline[lower] + (baseline[upper] - baseline[lower]) * (input - lower));
        }
        return output;
    }
}
