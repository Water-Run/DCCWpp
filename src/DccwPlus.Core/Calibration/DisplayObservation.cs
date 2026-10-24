using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DccwPlus.Core.Calibration;

// A saved observation is evidence of what Windows reported. It is not a command to change a display.
public sealed record DisplayObservation
{
    public required int SchemaVersion { get; init; }
    public required string Kind { get; init; }
    public required string Capability { get; init; }
    public required bool Applied { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required string Topology { get; init; }
    public required IReadOnlyList<DisplayInfo> Displays { get; init; }
}

public static class DisplayObservationCodec
{
    public const int MaxBytes = 256 * 1024;
    public static readonly string[] ColorStates = ["sdr", "wcg", "hdr", "advanced-color", "unknown", "not-probed"];
    public static readonly string[] Topologies = ["internal", "clone", "extend", "external", "unknown"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 16,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static DisplayObservation Capture(string topology, IReadOnlyList<DisplayInfo> displays, DateTimeOffset? capturedAt = null)
    {
        var observation = new DisplayObservation
        {
            SchemaVersion = 1,
            Kind = "display-observation",
            Capability = "read-only",
            Applied = false,
            CapturedAt = capturedAt ?? DateTimeOffset.UtcNow,
            Topology = topology,
            Displays = displays
        };
        Validate(observation);
        return observation;
    }

    public static DisplayObservation Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBytes) throw new InvalidDataException("Display observation exceeds 256 KiB.");
        try
        {
            var observation = JsonSerializer.Deserialize<DisplayObservation>(data, JsonOptions)
                ?? throw new InvalidDataException("Display observation is empty.");
            Validate(observation);
            return observation;
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid display observation JSON.", ex); }
    }

    public static byte[] Encode(DisplayObservation observation)
    {
        Validate(observation);
        return JsonSerializer.SerializeToUtf8Bytes(observation, JsonOptions);
    }

    public static void Validate(DisplayObservation observation)
    {
        if (observation.SchemaVersion != 1) throw new InvalidDataException("Unsupported display observation schema version.");
        if (observation.Kind != "display-observation") throw new InvalidDataException("Unsupported display observation kind.");
        if (observation.Capability != "read-only") throw new InvalidDataException("A display observation cannot request a display change.");
        if (observation.Applied) throw new InvalidDataException("A display observation cannot be marked applied.");
        if (observation.CapturedAt.Year is < 2000 or > 2100) throw new InvalidDataException("Display observation time is invalid.");
        if (!Topologies.Contains(observation.Topology)) throw new InvalidDataException("Unsupported display topology.");
        if (observation.Displays is null || observation.Displays.Count > 64) throw new InvalidDataException("Display observation must contain 0–64 displays.");
        if (observation.Displays.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != observation.Displays.Count)
            throw new InvalidDataException("Display observation contains a duplicate display ID.");
        foreach (var display in observation.Displays) ValidateDisplay(display, observation.Topology);
    }

    private static void ValidateDisplay(DisplayInfo display, string topology)
    {
        if (display.Id is null || display.Id.Length != 16 || !display.Id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException("Display ID must be 16 lowercase hex characters.");
        Text(display.Name, 128, "Display name");
        Text(display.DeviceName, 128, "Display device name");
        if (!ColorStates.Contains(display.ColorState)) throw new InvalidDataException("Unsupported display color state.");
        if (display.IdentityScope != "topology-query") throw new InvalidDataException("Display identity is limited to the current topology query.");
        if (display.Topology != topology) throw new InvalidDataException("Display topology does not match the observation.");
        if (display.TargetKey is not null && !IsTargetKey(display.TargetKey)) throw new InvalidDataException("Display target key is invalid.");
        if (display.Output is not null && !IsToken(display.Output)) throw new InvalidDataException("Display output name is invalid.");
        Range(display.Width, 1, 16384, "width");
        Range(display.Height, 1, 16384, "height");
        Range(display.PositionX, -100000, 100000, "positionX");
        Range(display.PositionY, -100000, 100000, "positionY");
        Range(display.RefreshMillihertz, 1, 1_000_000, "refreshMillihertz");
        Range(display.CloneGroup, 0, 65534, "cloneGroup");
        if (display.ProfileScope is not null && display.ProfileScope is not ("current-user" or "system-wide"))
            throw new InvalidDataException("Unsupported color profile scope.");
        if (display.ProfileName is not null)
        {
            Text(display.ProfileName, 255, "Color profile name");
            if (display.ProfileName is "." or ".." || display.ProfileName.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
                throw new InvalidDataException("Color profile name must be a file name, not a path or command.");
        }
    }

    private static bool IsTargetKey(string value)
    {
        var parts = value.Split(':');
        return parts.Length == 3
            && parts[0].Length == 8 && parts[1].Length == 8
            && parts[0].All(Uri.IsHexDigit) && parts[1].All(Uri.IsHexDigit)
            && parts[0] == parts[0].ToUpperInvariant() && parts[1] == parts[1].ToUpperInvariant()
            && uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsToken(string value) => value.Length is >= 1 and <= 40 && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static void Text(string? value, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
            throw new InvalidDataException($"{name} is invalid.");
    }

    private static void Range(int? value, int min, int max, string name)
    {
        if (value is int number && (number < min || number > max))
            throw new InvalidDataException($"{name} is outside the supported range.");
    }
}
