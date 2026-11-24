using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DccwPlus.Core.Calibration;

public sealed record DisplayCalibrationCapture(string DisplayId, string Name, bool IsPrimary, string ColorState,
    string? PersistenceKey, string? ProfileName, string? ProfileScope, string? SelectedProfileScope,
    string? ProfileQueryStatus, string? ProfileSha256, string CurveStatus, string? LogicalRampSha256);

public sealed record CalibrationCaptureReport(int SchemaVersion, DateTimeOffset CapturedAt, int WindowsBuild,
    string OsArchitecture, string Topology, IReadOnlyList<DisplayCalibrationCapture> Displays, string? TopologySha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsRemoteSession = null);

public static class CalibrationCaptureCodec
{
    private const int MaxBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static CalibrationCaptureReport Read(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Device)) != 0) throw InvalidCapture();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (!file.CanSeek || file.Length > MaxBytes) throw InvalidCapture();
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return Decode(bytes);
    }

    public static CalibrationCaptureReport Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes) throw InvalidCapture();
        try
        {
            // Windows PowerShell may redirect the capture to UTF-16 LE instead of UTF-8.
            string json = bytes.StartsWith<byte>([0xff, 0xfe])
                ? new UnicodeEncoding(false, false, true).GetString(bytes[2..])
                : new UTF8Encoding(false, true).GetString(bytes.StartsWith("\uFEFF"u8) ? bytes[3..] : bytes);
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 16, AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidCapture();
            CalibrationCaptureReport? report;
            if (root.TryGetProperty("ok", out _))
            {
                var envelope = root.Deserialize<Envelope>(Json);
                report = envelope is { SchemaVersion: 1, Ok: true } ? envelope.Data : null;
            }
            else report = root.Deserialize<CalibrationCaptureReport>(Json);
            if (report is null) throw InvalidCapture();
            Validate(report);
            return report;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        { throw new InvalidDataException("Invalid calibration capture.", ex); }
    }

    internal static void Validate(CalibrationCaptureReport report)
    {
        if (report.SchemaVersion != 1 || report.CapturedAt == default || report.WindowsBuild <= 0
            || !IsToken(report.OsArchitecture) || !DisplayObservationCodec.Topologies.Contains(report.Topology)
            || (report.TopologySha256 is not null && !ProfilePreviewPolicy.IsHash(report.TopologySha256))
            || report.Displays is null || report.Displays.Count > 64)
            throw InvalidCapture();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var display in report.Displays)
        {
            if (display is null || display.DisplayId is not { Length: 16 }
                || !display.DisplayId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') || !ids.Add(display.DisplayId)
                || string.IsNullOrWhiteSpace(display.Name) || display.Name.Length > 128 || display.Name.Any(char.IsControl)
                || !DisplayObservationCodec.ColorStates.Contains(display.ColorState) || !IsToken(display.CurveStatus)
                || (report.IsRemoteSession == true && display.CurveStatus != "remote_session_blocked")
                || (report.IsRemoteSession == false && display.CurveStatus == "remote_session_blocked")
                || (display.PersistenceKey is { } key && (key.Length != 69 || key[..5] is not ("edid:" or "path:")
                    || !ProfilePreviewPolicy.IsHash(key[5..]))))
                throw InvalidCapture();
            ValidateEvidence(display);
        }
    }

    private static void ValidateEvidence(DisplayCalibrationCapture display)
    {
        if (display.SelectedProfileScope is not (null or "current-user" or "system-wide")
            || display.ProfileScope is not (null or "current-user" or "system-wide")
            || display.ProfileQueryStatus is not (null or "read" or "not-found" or "unavailable")
            || (display.ProfileQueryStatus == "read") != (display.ProfileName is not null)
            || (display.ProfileQueryStatus == "read") != (display.ProfileScope is not null)
            || (display.ProfileQueryStatus is "read" or "not-found" && display.SelectedProfileScope is null)
            || (display.ProfileSha256 is not null && (display.ProfileName is null || !ProfilePreviewPolicy.IsHash(display.ProfileSha256)))
            || (display.CurveStatus == "logical_ramp_readable") != ProfilePreviewPolicy.IsHash(display.LogicalRampSha256)
            || (display.LogicalRampSha256 is not null && display.CurveStatus != "logical_ramp_readable"))
            throw InvalidCapture();
        if (display.ProfileName is { } profile) IccProfileInspector.ValidateFileName(profile);
    }

    private static bool IsToken(string? value) => value is { Length: >= 1 and <= 48 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z' or '-' or '_');

    private static InvalidDataException InvalidCapture() => new("Invalid calibration capture.");
    private sealed record Envelope(int SchemaVersion, bool Ok, CalibrationCaptureReport? Data);
}
