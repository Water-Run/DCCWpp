namespace DccwPlus.Core.Calibration;

// Header and tag-directory facts only. Tag presence is not a measurement of the panel.
public sealed record ProfileReport(
    string FileName,
    int ProfileBytes,
    string Version,
    string DeviceClass,
    string ColorSpace,
    string ConnectionSpace,
    bool SignatureValid,
    bool? VcgtPresent,
    bool? TrcPresent,
    bool? A2bPresent,
    bool? B2aPresent,
    string ReadStatus);

public static class IccProfileInspector
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public static readonly string[] ReadStatuses = ["read", "not-found", "too-large", "truncated", "unreadable"];

    public static ProfileReport Missing(string fileName) => Unknown(fileName, 0, "not-found");
    public static ProfileReport TooLarge(string fileName) => Unknown(fileName, 0, "too-large");
    public static ProfileReport Unreadable(string fileName) => Unknown(fileName, 0, "unreadable");

    public static ProfileReport Inspect(string fileName, ReadOnlySpan<byte> data)
    {
        ValidateFileName(fileName);
        if (data.Length is < 128 or > MaxBytes)
            return new(fileName, data.Length > MaxBytes ? 0 : data.Length, "unknown", "unknown", "unknown", "unknown", false, null, null, null, null, "truncated");
        int declared = ReadInt(data, 0);
        bool complete = declared == data.Length && declared >= 128 && declared <= MaxBytes;
        bool signature = data[36] == (byte)'a' && data[37] == (byte)'c' && data[38] == (byte)'s' && data[39] == (byte)'p';
        var tags = declared >= 128 ? ReadTags(data, Math.Min(data.Length, declared)) : ((bool?)null, (bool?)null, (bool?)null, (bool?)null);
        return new(fileName, data.Length, Version(data), FourCc(data, 12), FourCc(data, 16), FourCc(data, 20), signature, tags.Item1, tags.Item2, tags.Item3, tags.Item4, complete ? "read" : "truncated");
    }

    public static void ValidateReport(ProfileReport report)
    {
        ValidateFileName(report.FileName);
        if (!ReadStatuses.Contains(report.ReadStatus)) throw new InvalidDataException("Unsupported profile read status.");
        if (report.ProfileBytes is < 0 or > MaxBytes) throw new InvalidDataException("Profile size is outside the supported range.");
        if (report.ReadStatus is "not-found" or "too-large" or "unreadable" && report.ProfileBytes != 0)
            throw new InvalidDataException("An unread profile cannot report a byte count.");
        if (report.Version != "unknown" && !IsVersion(report.Version)) throw new InvalidDataException("Profile version is invalid.");
        Token(report.DeviceClass);
        Token(report.ColorSpace);
        Token(report.ConnectionSpace);
        if (report.ReadStatus is not ("read" or "truncated")
            && (report.SignatureValid || report.VcgtPresent is true || report.TrcPresent is true || report.A2bPresent is true || report.B2aPresent is true))
            throw new InvalidDataException("An unread profile cannot claim a signature or calibration tags.");
    }

    public static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != fileName.Trim() || fileName.Length > 255 || fileName.Any(char.IsControl)
            || fileName is "." or ".." || fileName.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new InvalidDataException("Color profile name must be a file name, not a path or command.");
    }

    private static ProfileReport Unknown(string fileName, int bytes, string status)
    {
        ValidateFileName(fileName);
        return new(fileName, bytes, "unknown", "unknown", "unknown", "unknown", false, null, null, null, null, status);
    }

    private static (bool? Vcgt, bool? Trc, bool? A2b, bool? B2a) ReadTags(ReadOnlySpan<byte> data, int limit)
    {
        if (limit < 132 || limit > data.Length) return (null, null, null, null);
        uint count = ReadUInt(data, 128);
        if (count > 4096 || 132L + count * 12L > limit) return (null, null, null, null);
        bool vcgt = false, red = false, green = false, blue = false, a2b = false, b2a = false;
        for (int index = 0; index < count; index++)
        {
            int entry = 132 + index * 12;
            uint offset = ReadUInt(data, entry + 4);
            uint size = ReadUInt(data, entry + 8);
            if (offset > (uint)limit || size > (uint)limit - offset) return (null, null, null, null);
            if (Matches(data, entry, "vcgt"u8)) vcgt = true;
            else if (Matches(data, entry, "rTRC"u8)) red = true;
            else if (Matches(data, entry, "gTRC"u8)) green = true;
            else if (Matches(data, entry, "bTRC"u8)) blue = true;
            else if (Matches(data, entry, "A2B0"u8) || Matches(data, entry, "A2B1"u8) || Matches(data, entry, "A2B2"u8)) a2b = true;
            else if (Matches(data, entry, "B2A0"u8) || Matches(data, entry, "B2A1"u8) || Matches(data, entry, "B2A2"u8)) b2a = true;
        }
        return (vcgt, red && green && blue, a2b, b2a);
    }

    private static string Version(ReadOnlySpan<byte> data)
    {
        int major = data[8];
        int minor = data[9] >> 4;
        int fix = data[9] & 0x0F;
        if (major > 99) return "unknown";
        return $"{major}.{minor}.{fix}";
    }

    private static string FourCc(ReadOnlySpan<byte> data, int offset)
    {
        Span<char> chars = stackalloc char[4];
        for (int index = 0; index < 4; index++)
        {
            byte value = data[offset + index];
            if (value is < 32 or > 126) return "unknown";
            chars[index] = (char)value;
        }
        string text = new string(chars).Trim();
        if (text.Length is < 1 or > 4 || text.Any(character => !char.IsAsciiLetterOrDigit(character))) return "unknown";
        return text;
    }

    private static bool Matches(ReadOnlySpan<byte> data, int offset, ReadOnlySpan<byte> signature) => data.Slice(offset, 4).SequenceEqual(signature);

    private static int ReadInt(ReadOnlySpan<byte> data, int offset)
    {
        uint value = ReadUInt(data, offset);
        return value > int.MaxValue ? -1 : (int)value;
    }

    private static uint ReadUInt(ReadOnlySpan<byte> data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static bool IsVersion(string value)
    {
        string[] parts = value.Split('.');
        return parts.Length == 3 && parts.All(part => part.Length is >= 1 and <= 2 && part.All(char.IsAsciiDigit));
    }

    private static void Token(string value)
    {
        if (value == "unknown") return;
        if (value.Length is < 1 or > 4 || value.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new InvalidDataException("Profile signature token is invalid.");
    }
}
