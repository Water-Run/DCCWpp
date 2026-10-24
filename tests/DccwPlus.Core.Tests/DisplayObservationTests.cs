using System.Text;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using Xunit;

namespace DccwPlus.Core.Tests;

public sealed class DisplayObservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dccwpp-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void ObservationRoundTripPreservesTheProbeAndStaysReadOnly()
    {
        var captured = DisplayObservationCodec.Capture("extend", [Sample(), Sample("fedcba9876543210") with { IsPrimary = false, PositionX = 1920 }],
            DateTimeOffset.Parse("2026-10-24T03:04:05Z"));
        var store = new DisplayObservationStore(_root);
        store.Save(captured);
        var loaded = DisplayObservationCodec.Decode(DisplayObservationCodec.Encode(captured));
        Assert.Equal(captured.CapturedAt, loaded.CapturedAt);
        Assert.Equal(captured.Displays, loaded.Displays);
        Assert.Equal(loaded.Displays, store.Load().Displays);
        Assert.False(loaded.Applied);
        Assert.Equal("read-only", loaded.Capability);
    }

    [Fact]
    public void HdrObservationDoesNotEnableCalibration()
    {
        var display = Sample() with { ColorState = "hdr", HdrActive = true, AdvancedColorActive = true };
        var observation = DisplayObservationCodec.Capture("extend", [display]);
        Assert.Equal("hdr", observation.Displays[0].ColorState);
        var plan = new CalibrationService(new FixedDisplays(observation.Displays)).Plan(CalibrationPreset.Create("Draft"), display.Id);
        Assert.False(plan.CanApply);
        Assert.Equal("calibration_backend_pending", plan.ReasonCode);
    }

    [Fact]
    public void ObservationRejectsCommandsAndUnsafeFields()
    {
        Assert.Throws<InvalidDataException>(() => DisplayObservationCodec.Capture("extend", [Sample() with { ColorState = "calibrated" }]));
        Assert.Throws<InvalidDataException>(() => DisplayObservationCodec.Capture("extend", [Sample() with { IdentityScope = "permanent" }]));
        Assert.Throws<InvalidDataException>(() => DisplayObservationCodec.Capture("extend", [Sample() with { ProfileName = @"C:\Windows\evil.icm" }]));
        Assert.Throws<InvalidDataException>(() => DisplayObservationCodec.Capture("internal", [Sample()]));
        string json = Encoding.UTF8.GetString(DisplayObservationCodec.Encode(DisplayObservationCodec.Capture("extend", [Sample()])));
        Assert.Throws<InvalidDataException>(() => DisplayObservationCodec.Decode(Encoding.UTF8.GetBytes(json.Replace("\"applied\": false", "\"applied\": true"))));
    }

    [Fact]
    public void UnknownObservationFieldIsRejected()
    {
        string json = Encoding.UTF8.GetString(DisplayObservationCodec.Encode(DisplayObservationCodec.Capture("extend", [Sample()])));
        string modified = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1,\n  \"command\": \"malicious.exe\"");
        Assert.Throws<InvalidDataException>(() => DisplayObservationCodec.Decode(Encoding.UTF8.GetBytes(modified)));
    }

    [Fact]
    public void MissingObservationIsNotInvented() =>
        Assert.Throws<FileNotFoundException>(() => new DisplayObservationStore(_root).Load());

    private static DisplayInfo Sample(string id = "0123456789abcdef") => new(
        id, "Monitor", @"\\.\DISPLAY1", true, "sdr", "topology-query", "extend", "00000000:00000001:256",
        "hdmi", 1920, 1080, 0, 0, 60000, null, "sRGB Color Space Profile.icm", "current-user", false, false);

    private sealed class FixedDisplays(IReadOnlyList<DisplayInfo> displays) : IDisplayService
    {
        public IReadOnlyList<DisplayInfo> Enumerate() => displays;
    }
}
