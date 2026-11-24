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

    [Fact]
    public void RefreshFollowsTheSameUniqueDisplayEvenWhenItsOldPathIsReused()
    {
        var selected = SelectedDisplay();
        var replacement = selected with { PersistenceKey = "edid:" + new string('B', 64) };
        var moved = selected with { Id = "new-path", TargetKey = "new-target", DeviceName = "DISPLAY3", SourceId = 3 };

        Assert.Same(moved, MonitorIdentity.ResolveSelectedDisplay(selected, [replacement, moved]));
        Assert.Null(MonitorIdentity.ResolveSelectedDisplay(selected, [replacement]));
        Assert.Null(MonitorIdentity.ResolveSelectedDisplay(null, [replacement]));
    }

    [Fact]
    public void RefreshKeepsAnExactSelectionAndDeclinesAnAmbiguousReassignment()
    {
        var selected = SelectedDisplay();
        var other = selected with { Id = "other-path" };
        var duplicate = selected with { Id = "duplicate-path" };
        var exact = selected with { Width = 1920 };

        Assert.Same(exact, MonitorIdentity.ResolveSelectedDisplay(selected, [other, exact]));
        Assert.Null(MonitorIdentity.ResolveSelectedDisplay(selected, [other, duplicate]));
    }

    [Fact]
    public void WithoutPersistentIdentityRefreshRequiresTheSameSourceAndProfileDevice()
    {
        var selected = SelectedDisplay() with { PersistenceKey = null };
        var samePath = selected with { Width = 1920, ProfileDeviceKey = selected.ProfileDeviceKey!.ToUpperInvariant() };

        Assert.Same(samePath, MonitorIdentity.ResolveSelectedDisplay(selected, [samePath]));
        Assert.Null(MonitorIdentity.ResolveSelectedDisplay(selected, [samePath with { SourceId = 5 }]));
        Assert.Null(MonitorIdentity.ResolveSelectedDisplay(selected, [samePath with { ProfileDeviceKey = "new-device" }]));
    }

    private static DisplayInfo SelectedDisplay() => new("selected-path", "Selected display", "DISPLAY1", false, "sdr",
        TargetKey: "selected-target", SourceId: 1, ProfileDeviceKey: "selected-device", PersistenceKey: "edid:" + new string('A', 64));

    private static byte[] Edid(uint serial)
    {
        var bytes = new byte[128]; new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
        bytes[8] = 0x4C; bytes[9] = 0x2D; bytes[10] = 0xAA;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), serial);
        bytes[127] = unchecked((byte)-bytes.Sum(b => (int)b)); return bytes;
    }
}
