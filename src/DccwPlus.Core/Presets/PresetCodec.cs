using System.Text.Json;
using System.Text.Json.Serialization;

namespace DccwPlus.Core.Presets;

public static class PresetCodec
{
    public const int MaxBytes = 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 16,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false
    };

    public static CalibrationPreset Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBytes) throw new InvalidDataException("Preset exceeds 1 MiB.");
        try
        {
            var preset = JsonSerializer.Deserialize<CalibrationPreset>(data, JsonOptions)
                ?? throw new InvalidDataException("Preset is empty.");
            Validate(preset);
            return preset;
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid preset JSON.", ex); }
    }

    public static void Validate(CalibrationPreset preset)
    {
        if (preset.SchemaVersion != 1) throw new InvalidDataException("Unsupported preset schema version.");
        if (preset.Id == Guid.Empty) throw new InvalidDataException("Preset ID must not be empty.");
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 100)
            throw new InvalidDataException("Preset name must contain 1–100 characters.");
        if (preset.Description is null || preset.Description.Length > 2000 || preset.Author is null || preset.Author.Length > 200)
            throw new InvalidDataException("Invalid preset metadata.");
        if (preset.CreatedAt == default) throw new InvalidDataException("Preset creation date is required.");
        var c = preset.Calibration ?? throw new InvalidDataException("Calibration is required.");
        if (c.ColorMode != "sdr") throw new InvalidDataException("Only SDR preset intent is supported in schema 1.");
        Range(c.Gamma, 1, 3, "gamma");
        Range(c.RedGain, 0.5, 1.5, "redGain");
        Range(c.GreenGain, 0.5, 1.5, "greenGain");
        Range(c.BlueGain, 0.5, 1.5, "blueGain");
    }

    private static void Range(double value, double min, double max, string name)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            throw new InvalidDataException($"{name} must be between {min} and {max}.");
    }

    public static byte[] Encode(CalibrationPreset preset)
    {
        Validate(preset);
        return JsonSerializer.SerializeToUtf8Bytes(preset, JsonOptions);
    }

    public static byte[] ReadBounded(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxBytes) throw new InvalidDataException("Preset exceeds 1 MiB.");
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int count;
        while ((count = file.Read(block)) > 0)
        {
            if (buffer.Length + count > MaxBytes) throw new InvalidDataException("Preset exceeds 1 MiB.");
            buffer.Write(block, 0, count);
        }
        return buffer.ToArray();
    }
}
