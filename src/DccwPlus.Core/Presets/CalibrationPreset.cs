namespace DccwPlus.Core.Presets;

// Portable intent only. Display associations and local paths never travel with a preset.
public sealed record CalibrationPreset
{
    public required int SchemaVersion { get; init; }
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public required DateTimeOffset CreatedAt { get; init; }
    public required SdrCalibration Calibration { get; init; }

    public static CalibrationPreset Create(string name, SdrCalibration? calibration = null) => new()
    {
        SchemaVersion = 1,
        Id = Guid.NewGuid(),
        Name = name.Trim(),
        CreatedAt = DateTimeOffset.UtcNow,
        Calibration = calibration ?? new()
    };
}

public sealed record SdrCalibration
{
    public string ColorMode { get; init; } = "sdr";
    public double Gamma { get; init; } = 2.2;
    public double RedGain { get; init; } = 1;
    public double GreenGain { get; init; } = 1;
    public double BlueGain { get; init; } = 1;
}
