using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DccwPlus.Core.Calibration;

public sealed record MonitorPersistenceIdentity(string Key, string Kind);
public static class MonitorIdentity
{
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
