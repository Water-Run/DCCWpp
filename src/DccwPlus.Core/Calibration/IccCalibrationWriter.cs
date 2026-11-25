using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DccwPlus.Core.Presets;

namespace DccwPlus.Core.Calibration;

public static class IccCalibrationWriter
{
    // Preserve the source characterization. Replace only the calibration and display name.
    public static byte[] Build(ReadOnlySpan<byte> original, GammaRamp ramp, Guid id, string? presetName = null)
    {
        ramp.Validate();
        var report = IccProfileInspector.Inspect("source.icc", original);
        if (report.ReadStatus != "read" || !report.SignatureValid || report.DeviceClass != "mntr"
            || report.ColorSpace != "RGB" || original[8] is not (2 or 4) || report.TrcPresent is null)
            throw new InvalidDataException("The base ICC is not a supported RGB display profile.");
        int count = checked((int)Read(original, 128));
        int directoryEnd = checked(132 + count * 12);
        var tags = new List<(string Signature, byte[] Data)>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        int dataBytes = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = 132 + 12 * i;
            string signature = Encoding.ASCII.GetString(original.Slice(entry, 4));
            int offset = checked((int)Read(original, entry + 4)), length = checked((int)Read(original, entry + 8));
            if (!signatures.Add(signature) || offset < directoryEnd || length < 8 || offset % 4 != 0
                || offset > original.Length || length > original.Length - offset)
                throw new InvalidDataException("Invalid or duplicate ICC tag entry.");
            if (signature == "MHC2") throw new InvalidDataException("MHC2 profiles require the hardware calibration backend.");
            if (signature is "vcgt" or "MS00" or "desc") continue;
            dataBytes += Align(length);
            if (dataBytes > IccProfileInspector.MaxBytes)
                throw new InvalidDataException("The calibrated ICC exceeds the supported size.");
            tags.Add((signature, original.Slice(offset, length).ToArray()));
        }
        tags.Add(("desc", Description(id, presetName, original[8] == 4)));
        tags.Add(("vcgt", Vcgt(ramp)));
        int position = Align(132 + tags.Count * 12);
        int outputBytes = position + tags.Sum(t => Align(t.Data.Length));
        if (outputBytes > IccProfileInspector.MaxBytes)
            throw new InvalidDataException("The calibrated ICC exceeds the supported size.");
        var output = new byte[outputBytes];
        original[..128].CopyTo(output);
        Write(output, 0, (uint)output.Length);
        output.AsSpan(84, 16).Clear();
        Write(output, 128, (uint)tags.Count);
        for (int i = 0; i < tags.Count; i++)
        {
            int entry = 132 + i * 12;
            Encoding.ASCII.GetBytes(tags[i].Signature, output.AsSpan(entry, 4));
            Write(output, entry + 4, (uint)position);
            Write(output, entry + 8, (uint)tags[i].Data.Length);
            tags[i].Data.CopyTo(output, position);
            position += Align(tags[i].Data.Length);
        }
        if (original[8] == 4)
        {
            var digestInput = (byte[])output.Clone();
            digestInput.AsSpan(44, 4).Clear(); digestInput.AsSpan(64, 4).Clear();
            MD5.HashData(digestInput).CopyTo(output, 84); // ICC v4 profile ID, not a security digest.
        }
        return output;
    }
    public static byte[] Vcgt(GammaRamp ramp)
    {
        ramp.Validate();
        var data = new byte[18 + 3 * 256 * 2];
        "vcgt"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(14), 256);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), 2);
        int offset = 18;
        foreach (var channel in new[] { ramp.Red, ramp.Green, ramp.Blue })
            foreach (ushort value in channel)
            { BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value); offset += 2; }
        return data;
    }
    private static byte[] Description(Guid id, string? presetName, bool version4)
    {
        string identity = "DCCW++ " + id.ToString("N")[..12];
        string label = identity;
        if (presetName is { Length: > 0 })
        {
            if (presetName.Length > PresetCodec.MaxNameLength) throw new InvalidDataException("The ICC preset name is too long.");
            var characters = presetName.Select(character => char.IsControl(character) ? ' ' : character).ToArray();
            string name = string.Join(' ', new string(characters).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (name.Length > 0) label += " - " + name;
        }
        if (version4)
        {
            byte[] text = Encoding.BigEndianUnicode.GetBytes(label);
            var data = new byte[28 + text.Length];
            "mluc"u8.CopyTo(data); Write(data, 8, 1); Write(data, 12, 12);
            "enUS"u8.CopyTo(data.AsSpan(16)); Write(data, 20, (uint)text.Length); Write(data, 24, 28);
            text.CopyTo(data, 28); return data;
        }
        // The v2 invariant field is ASCII. Keep the complete name in the Unicode field for capable readers.
        bool asciiOnly = label.All(char.IsAscii);
        byte[] ascii = Encoding.ASCII.GetBytes((asciiOnly ? label : identity) + "\0");
        byte[] unicode = asciiOnly ? [] : Encoding.BigEndianUnicode.GetBytes(label + "\0");
        int unicodeHeader = 12 + ascii.Length;
        var result = new byte[unicodeHeader + 8 + unicode.Length + 70];
        "desc"u8.CopyTo(result); Write(result, 8, (uint)ascii.Length); ascii.CopyTo(result, 12);
        Write(result, unicodeHeader + 4, (uint)(unicode.Length / sizeof(char)));
        unicode.CopyTo(result, unicodeHeader + 8);
        return result;
    }
    private static int Align(int value) => checked((value + 3) & ~3);
    private static uint Read(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
    private static void Write(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes[offset..], value);
}
