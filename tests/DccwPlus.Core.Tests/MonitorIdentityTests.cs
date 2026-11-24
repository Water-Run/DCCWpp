using System.Buffers.Binary;
using DccwPlus.Core.Calibration;
using Xunit;

namespace DccwPlus.Core.Tests;

public sealed class MonitorIdentityTests
{
    [Fact] public void ARealEdidSerialSurvivesPortChanges()
    {
        byte[] edid = Edid(1234);
        var first = MonitorIdentity.FromEdid(edid, @"\\?\DISPLAY#ABC#PORT1");
        var second = MonitorIdentity.FromEdid(edid, @"\\?\DISPLAY#ABC#PORT2");
        Assert.Equal(first, second); Assert.Equal("edid-serial", first.Kind);
        Assert.NotEqual(first.Key, MonitorIdentity.FromEdid(Edid(5678), @"\\?\DISPLAY#ABC#PORT1").Key);
    }
    [Fact] public void MissingOrCorruptSerialUsesAnExplicitPortSpecificIdentity()
    {
        var first = MonitorIdentity.FromEdid(Edid(0), @"\\?\DISPLAY#ABC#PORT1");
        var second = MonitorIdentity.FromEdid(Edid(0), @"\\?\DISPLAY#ABC#PORT2");
        Assert.NotEqual(first.Key, second.Key); Assert.Equal("device-path", first.Kind);
        byte[] invalid = Edid(1234); invalid[20] ^= 1;
        Assert.Equal("device-path", MonitorIdentity.FromEdid(invalid, "device").Kind);
    }
    private static byte[] Edid(uint serial)
    {
        var bytes = new byte[128]; new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
        bytes[8] = 0x4C; bytes[9] = 0x2D; bytes[10] = 0xAA;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), serial);
        bytes[127] = unchecked((byte)-bytes.Sum(b => (int)b)); return bytes;
    }
}
