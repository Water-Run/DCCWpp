using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace DccwPlus.Core.Calibration;

public sealed record MonitorPersistenceIdentity(string Key, string Kind);
public static class MonitorIdentity
{
    public static bool IsPersistenceKey([NotNullWhen(true)] string? key) =>
        key is { Length: 69 }
        && (key.StartsWith("edid:", StringComparison.Ordinal) || key.StartsWith("path:", StringComparison.Ordinal))
        && ProfilePreviewPolicy.IsHash(key[5..]);

    public static bool SamePersistentIdentity(string? saved, string? current) =>
        IsPersistenceKey(saved) && string.Equals(saved, current, StringComparison.OrdinalIgnoreCase);

    public static bool SameProfileDevice(string? savedIdentity, string? currentIdentity, string? savedDevice, string? currentDevice) =>
        SamePersistentIdentity(savedIdentity, currentIdentity) && ProfilePreviewPolicy.IsHash(savedDevice)
        && string.Equals(savedDevice, currentDevice, StringComparison.OrdinalIgnoreCase);

    public static bool MatchesSavedDisplay(GammaDeviceState saved, DisplayInfo current) =>
        saved.PersistenceKey is not null ? SamePersistentIdentity(saved.PersistenceKey, current.PersistenceKey)
            : saved.DisplayId == current.Id && saved.TargetKey == current.TargetKey && saved.DeviceName == current.DeviceName
              && (saved.SourceId is null || saved.SourceId == current.SourceId);

    public static IEnumerable<string> AmbiguousPersistenceKeys(IReadOnlyList<DisplayInfo> connected) =>
        connected.Select(display => display.PersistenceKey).OfType<string>().Where(IsPersistenceKey)
            .GroupBy(key => key, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key);

    public static DisplayInfo? ResolveSelectedDisplay(DisplayInfo? selected, IReadOnlyList<DisplayInfo> connected,
        bool hadAmbiguousIdentity = false)
    {
        if (selected is null) return null;
        var exact = connected.FirstOrDefault(display => display.Id == selected.Id);
        if (selected.PersistenceKey is not { } key)
            return exact is not null && SameActivePath(selected, exact) ? exact : null;

        var matches = connected.Where(display => SamePersistentIdentity(key, display.PersistenceKey)).Take(2).ToArray();
        bool ambiguous = hadAmbiguousIdentity || matches.Length > 1;
        if (exact is not null && SamePersistentIdentity(key, exact.PersistenceKey))
            return !ambiguous || SameActivePath(selected, exact) ? exact : null;

        // The remaining monitor may be the other device that shared this identity.
        return !ambiguous && matches.Length == 1 ? matches[0] : null;
    }

    private static bool SameActivePath(DisplayInfo selected, DisplayInfo current) =>
        selected.TargetKey == current.TargetKey && selected.SourceId == current.SourceId
        && string.Equals(selected.DeviceName, current.DeviceName, StringComparison.OrdinalIgnoreCase)
        && (selected.ProfileDeviceKey is null || string.Equals(selected.ProfileDeviceKey, current.ProfileDeviceKey, StringComparison.OrdinalIgnoreCase));

    public static MonitorPersistenceIdentity FromEdid(ReadOnlySpan<byte> edid, string devicePath)
    {
        if (edid.Length >= 128 && edid[..8].SequenceEqual(new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 })
            && edid[..128].ToArray().Sum(b => (int)b) % 256 == 0)
        {
            uint serial = BinaryPrimitives.ReadUInt32LittleEndian(edid[12..]);
            string? serialText = serial is not (0 or uint.MaxValue) ? serial.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            if (serialText is null)
                for (int offset = 54; offset <= 108; offset += 18)
                    if (edid[offset] == 0 && edid[offset + 1] == 0 && edid[offset + 2] == 0 && edid[offset + 3] == 255)
                    {
                        string value = Encoding.ASCII.GetString(edid.Slice(offset + 5, 13)).Trim('\0', '\n', '\r', ' ');
                        if (value.Length > 0 && value != "0" && value.All(c => c is >= ' ' and <= '~')) serialText = value;
                    }
            if (serialText is not null)
            {
                string manufacturerProduct = Convert.ToHexString(edid.Slice(8, 4));
                return new("edid:" + Hash(manufacturerProduct + ":" + serialText), "edid-serial");
            }
        }
        if (string.IsNullOrWhiteSpace(devicePath)) throw new InvalidDataException("A monitor device path is required.");
        return new("path:" + Hash(devicePath.Trim().ToUpperInvariant()), "device-path");
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
