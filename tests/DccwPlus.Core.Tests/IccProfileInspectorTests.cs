using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using Xunit;

namespace DccwPlus.Core.Tests;

public sealed class IccProfileInspectorTests
{
    [Fact]
    public void HeaderAndCalibrationTagsAreReportedWithoutLoadingCurves()
    {
        var report = IccProfileInspector.Inspect("panel.icc", Profile("vcgt", "rTRC", "gTRC", "bTRC", "A2B0", "B2A0"));
        Assert.Equal("read", report.ReadStatus);
        Assert.Equal("4.3.0", report.Version);
        Assert.Equal("mntr", report.DeviceClass);
        Assert.Equal("RGB", report.ColorSpace);
        Assert.Equal("XYZ", report.ConnectionSpace);
        Assert.True(report.SignatureValid);
        Assert.True(report.VcgtPresent);
        Assert.True(report.TrcPresent);
        Assert.True(report.A2bPresent);
        Assert.True(report.B2aPresent);
        IccProfileInspector.ValidateReport(report);
    }

    [Fact]
    public void MissingSignatureStillExposesAReadableTagDirectory()
    {
        var report = IccProfileInspector.Inspect("panel.icc", Profile(false, false, "vcgt", "rTRC", "gTRC"));
        Assert.False(report.SignatureValid);
        Assert.True(report.VcgtPresent);
        Assert.False(report.TrcPresent);
        Assert.False(report.A2bPresent);
        Assert.False(report.B2aPresent);
        Assert.Equal("read", report.ReadStatus);
    }

    [Fact]
    public void IncompleteProfilesDoNotInventTagFacts()
    {
        var shortHeader = IccProfileInspector.Inspect("panel.icc", new byte[40]);
        Assert.Equal("truncated", shortHeader.ReadStatus);
        Assert.Equal(40, shortHeader.ProfileBytes);
        Assert.False(shortHeader.SignatureValid);
        Assert.Null(shortHeader.VcgtPresent);
        Assert.Null(shortHeader.TrcPresent);
        Assert.Equal("unknown", shortHeader.Version);

        var shifted = IccProfileInspector.Inspect("panel.icc", Profile(true, true, "vcgt", "rTRC", "gTRC", "bTRC"));
        Assert.Equal("read", shifted.ReadStatus);
        Assert.True(shifted.SignatureValid);
        Assert.Null(shifted.VcgtPresent);
        Assert.Null(shifted.A2bPresent);

        byte[] declared = Profile("vcgt");
        declared[3] = (byte)(declared.Length + 8);
        var mismatch = IccProfileInspector.Inspect("panel.icc", declared);
        Assert.Equal("truncated", mismatch.ReadStatus);
        Assert.True(mismatch.VcgtPresent);
        Assert.Equal("mntr", mismatch.DeviceClass);

        var huge = GC.AllocateUninitializedArray<byte>(IccProfileInspector.MaxBytes + 1);
        var oversized = IccProfileInspector.Inspect("big.icc", huge);
        Assert.Equal(0, oversized.ProfileBytes);
        Assert.Equal("truncated", oversized.ReadStatus);
        Assert.Null(oversized.B2aPresent);
        IccProfileInspector.ValidateReport(oversized);
    }

    [Fact]
    public void ObservationRejectsAMismatchedOrUnreadProfileClaim()
    {
        var display = Sample();
        var other = Inspected("other.icc");
        Assert.Throws<InvalidDataException>(() => DisplayObservationCodec.Capture("extend", [display with { Profile = other }]));
        var unread = new ProfileReport(display.ProfileName!, 0, "unknown", "unknown", "unknown", "unknown", true, true, null, null, null, "not-found");
        Assert.Throws<InvalidDataException>(() => DisplayObservationCodec.Capture("extend", [display with { Profile = unread }]));
        Assert.Throws<InvalidDataException>(() => IccProfileInspector.ValidateFileName(@"C:\Windows\color\evil.icc"));
        Assert.Throws<InvalidDataException>(() => IccProfileInspector.ValidateFileName(" spaced.icc"));

        var matching = display with { Profile = Inspected(display.ProfileName!) };
        var roundTrip = DisplayObservationCodec.Decode(DisplayObservationCodec.Encode(DisplayObservationCodec.Capture("extend", [matching])));
        Assert.Equal(matching.Profile, roundTrip.Displays[0].Profile);
        Assert.False(roundTrip.Applied);
    }

    [Theory]
    [InlineData("not-probed", null, null)]
    [InlineData("hdr", true, true)]
    [InlineData("sdr", null, false)]
    [InlineData("sdr", false, null)]
    [InlineData("wcg", false, false)]
    public void NonExplicitSdrNeverReachesThePendingBackend(string color, bool? hdr, bool? advanced)
    {
        var display = Sample() with { ColorState = color, HdrActive = hdr, AdvancedColorActive = advanced };
        var plan = new CalibrationService(new FixedDisplays(display)).Plan(CalibrationPreset.Create("Draft"), display.Id);
        Assert.False(plan.CanApply);
        Assert.Equal("sdr_path_blocked", plan.ReasonCode);
    }

    [Fact]
    public void ExplicitSdrStaysPendingAndRestoreNeverWrites()
    {
        var current = Sample();
        var plan = new CalibrationService(new FixedDisplays(current)).Plan(CalibrationPreset.Create("Draft"), current.Id);
        Assert.False(plan.CanApply);
        Assert.Equal("calibration_backend_pending", plan.ReasonCode);

        var saved = DisplayObservationCodec.Capture("extend", [current]);
        Assert.Equal("association_matches", RestorePlanner.Plan(current, saved).ReasonCode);
        Assert.False(RestorePlanner.Plan(current, saved).CanRestore);
        Assert.Equal("observation_not_found", RestorePlanner.Plan(current, null).ReasonCode);
        Assert.Equal("observation_missing_display", RestorePlanner.Plan(current, DisplayObservationCodec.Capture("extend", [Sample("fedcba9876543210")])).ReasonCode);
        Assert.Equal("topology_changed", RestorePlanner.Plan(current with { Topology = "clone" }, saved).ReasonCode);
        Assert.Equal("sdr_path_blocked", RestorePlanner.Plan(current with { ColorState = "hdr", HdrActive = true }, saved).ReasonCode);
        Assert.Equal("profile_not_recorded", RestorePlanner.Plan(current, DisplayObservationCodec.Capture("extend", [current with { ProfileName = null, ProfileScope = null, Profile = null }])).ReasonCode);
        var renamed = RestorePlanner.Plan(current with { ProfileName = "other.icc", Profile = null }, saved);
        Assert.Equal("restore_backend_pending", renamed.ReasonCode);
        Assert.False(renamed.CanRestore);
        Assert.Contains("Do not load vcgt in this build", renamed.Steps);
    }

    private static ProfileReport Inspected(string fileName) => IccProfileInspector.Inspect(fileName, Profile("vcgt"));

    private static DisplayInfo Sample(string id = "0123456789abcdef") => new(
        id, "Monitor", @"\\.\DISPLAY1", true, "sdr", "topology-query", "extend", "00000000:00000001:256",
        "hdmi", 1920, 1080, 0, 0, 60000, null, "sRGB Color Space Profile.icm", "current-user", false, false);

    private static byte[] Profile(params string[] tags) => Profile(true, false, tags);

    private static byte[] Profile(bool signature = true, bool corruptOffset = false, params string[] tags)
    {
        int count = tags.Length;
        int table = 132 + count * 12;
        int total = table + count * 4;
        var data = new byte[total];
        WriteUInt(data, 0, (uint)total);
        data[8] = 4;
        data[9] = 0x30;
        WriteFour(data, 12, "mntr");
        WriteFour(data, 16, "RGB ");
        WriteFour(data, 20, "XYZ ");
        if (signature) WriteFour(data, 36, "acsp");
        WriteUInt(data, 128, (uint)count);
        for (int index = 0; index < count; index++)
        {
            int entry = 132 + index * 12;
            WriteFour(data, entry, tags[index]);
            uint offset = corruptOffset && index == 0 ? (uint)(total + 10) : (uint)(table + index * 4);
            WriteUInt(data, entry + 4, offset);
            WriteUInt(data, entry + 8, 4);
        }
        return data;
    }

    private static void WriteFour(byte[] data, int offset, string text)
    {
        for (int index = 0; index < 4; index++) data[offset + index] = index < text.Length ? (byte)text[index] : (byte)' ';
    }

    private static void WriteUInt(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private sealed class FixedDisplays(DisplayInfo display) : IDisplayService
    {
        public IReadOnlyList<DisplayInfo> Enumerate() => [display];
    }
}
