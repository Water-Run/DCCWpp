using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using Xunit;

namespace DccwPlus.Core.Tests;

public sealed class BindingRestoreTests
{
    [Fact]
    public void OptInRestoresTheConfirmedAbsoluteCurveAndSubsequentRunsAreNoOps()
    {
        var test = new Fixture();

        Assert.Equal("planned", test.Restorer.Restore(test.Binding, dryRun: true).Status);
        Assert.Equal(0, test.Backend.Writes);
        Assert.Empty(test.Journal.Sessions);
        Assert.Equal("restored", test.Restorer.Restore(test.Binding, dryRun: false).Status);
        Assert.True(test.Backend.State.Ramp.Matches(test.Binding.Ramp));
        Assert.Equal("confirmed", test.Journal.Sessions[^1].Status);
        Assert.True(test.Journal.Sessions[^1].IsBindingRestore);

        int writes = test.Backend.Writes;
        Assert.Equal("unchanged", test.Restorer.Restore(test.Binding, dryRun: false).Status);
        Assert.Equal(writes, test.Backend.Writes);
    }

    [Theory]
    [InlineData("binding_disabled")]
    [InlineData("display_disconnected")]
    [InlineData("display_identity_ambiguous")]
    [InlineData("shared_source_blocked")]
    [InlineData("sdr_path_blocked")]
    [InlineData("profile_selection_changed")]
    [InlineData("profile_file_changed")]
    [InlineData("external_curve_change")]
    public void RestoreSkipsUnsafeOrUnrequestedChanges(string expectedReason)
    {
        var test = new Fixture();
        var binding = test.Binding;
        var state = test.Backend.State;
        var display = Assert.Single(test.Displays.Items);
        switch (expectedReason)
        {
            case "binding_disabled": binding = binding with { RestoreEnabled = false }; break;
            case "display_disconnected": test.Displays.Items = []; break;
            case "display_identity_ambiguous": test.Displays.Items = [display, display with { Id = "000001234567abcd" }]; break;
            case "shared_source_blocked": test.Displays.Items = [display with { Topology = "clone" }]; break;
            case "sdr_path_blocked": test.Displays.Items = [display with { ColorState = "hdr", HdrActive = true }]; break;
            case "profile_selection_changed": test.Backend.State = state with { ProfileName = "another.icc" }; break;
            case "profile_file_changed": test.ProfileHash = new string('B', 64); break;
            case "external_curve_change":
                test.Backend.State = state with { Ramp = GammaRampMath.Compose(GammaRamp.Identity(), new() { BlueGain = .7 }) };
                break;
        }

        var result = test.Restorer.Restore(binding, dryRun: false);

        Assert.Equal("skipped", result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Equal(0, test.Backend.Writes);
        Assert.Empty(test.Journal.Sessions);
    }

    [Fact]
    public void SourceChangeBetweenPreflightAndTransactionCannotWriteTheDisplay()
    {
        var test = new Fixture();
        var state = test.Backend.State;
        test.Backend.OnRead = count => count == 1 ? state : state with { SourceId = 5 };

        var result = test.Restorer.Restore(test.Binding, dryRun: false);

        Assert.Equal("display_state_changed", result.ReasonCode);
        Assert.Equal(0, test.Backend.Writes);
        Assert.Empty(test.Journal.Sessions);
    }

    [Fact]
    public void RestoreCannotReportSuccessIfTheSameProfileMovesToSystemScope()
    {
        var test = new Fixture();
        test.ReadHash = () =>
        {
            if (test.Journal.Sessions.LastOrDefault()?.Status == "committing")
                test.Backend.State = test.Backend.State with { ProfileScope = "system-wide", SelectedProfileScope = "system-wide" };
            return test.ProfileHash;
        };

        var result = test.Restorer.Restore(test.Binding, dryRun: false);

        Assert.Equal("skipped", result.Status);
        Assert.Equal("profile_selection_changed", result.ReasonCode);
        Assert.Equal("superseded", test.Journal.Sessions[^1].Status);
        Assert.Equal("system-wide", test.Backend.State.SelectedProfileScope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableIccIsReportedSeparatelyFromUnavailablePresetData(bool dryRun)
    {
        var test = new Fixture();
        test.ReadHash = () => throw new IOException("The installed ICC could not be read.");

        var result = test.Restorer.Restore(test.Binding, dryRun);

        Assert.Equal("failed", result.Status);
        Assert.Equal("profile_file_unavailable", result.ReasonCode);
        Assert.Equal(0, test.Backend.Writes);
        Assert.Empty(test.Journal.Sessions);

        test.ReadHash = null;
        test.ReadPreset = () => throw new FileNotFoundException("The local preset is missing.");
        Assert.Equal("restore_data_unavailable", test.Restorer.Restore(test.Binding, dryRun).ReasonCode);
        Assert.Equal(0, test.Backend.Writes);

        test.ReadPreset = null;
        Assert.Equal(dryRun ? "planned" : "restored", test.Restorer.Restore(test.Binding, dryRun).Status);
        if (dryRun) Assert.Equal(0, test.Backend.Writes);
        else Assert.True(test.Backend.State.Ramp.Matches(test.Binding.Ramp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHardwareCalibrationProfileBlocksAutomaticRestoreAndItsDryRun(bool dryRun)
    {
        var test = new Fixture();
        test.Backend.State = test.Backend.State with { ProfileMhc2Present = true };

        var result = test.Restorer.Restore(test.Binding, dryRun);

        Assert.Equal("skipped", result.Status);
        Assert.Equal("mhc2_profile_blocked", result.ReasonCode);
        Assert.Equal(0, test.Backend.Writes);
        Assert.Empty(test.Journal.Sessions);
    }

    private sealed class Fixture
    {
        public FakeDisplays Displays { get; } = new();
        public FakeBackend Backend { get; }
        public Journal Journal { get; } = new();
        public CalibrationBindingRestorer Restorer { get; }
        public CalibrationBinding Binding { get; }
        public string ProfileHash { get; set; } = new('A', 64);
        public Func<string>? ReadHash { get; set; }
        public Func<CalibrationPreset>? ReadPreset { get; set; }

        public Fixture()
        {
            string key = "path:" + new string('C', 64);
            string name = "DCCWPlus-" + Guid.NewGuid().ToString("N") + ".icc";
            var preset = CalibrationPreset.Create("Daylight", new() { Gamma = 2.5 });
            var original = GammaRampMath.Compose(GammaRamp.Identity(), new() { Gamma = 2.05 });
            Binding = new()
            {
                PersistenceKey = key, IdentityKind = "device-path", DisplayName = "SDR display",
                PresetId = preset.Id, ProfileName = name, ProfileSha256 = ProfileHash,
                Ramp = GammaRampMath.Compose(original, preset.Calibration), OriginalRamp = original.Copy(),
                RestoreEnabled = true, SavedAt = DateTimeOffset.UtcNow
            };
            const string id = "0123456789abcdef", device = @"\\.\DISPLAY1", target = "00000000:00000001:1";
            Displays.Items = [new(id, Binding.DisplayName, device, true, "sdr", Topology: "extend",
                TargetKey: target, HdrActive: false, AdvancedColorActive: false,
                SourceId: 1, PersistenceKey: key, PersistenceKind: "device-path")];
            Backend = new(new(id, device, target, new string('D', 64), true, false,
                original, name, "current-user", SourceId: 1, PersistenceKey: key, SelectedProfileScope: "current-user"));
            Restorer = new(Displays, Backend, Journal, _ => ReadHash?.Invoke() ?? ProfileHash, _ => ReadPreset?.Invoke() ?? preset);
        }
    }

    private sealed class FakeDisplays : IDisplayService
    {
        public IReadOnlyList<DisplayInfo> Items { get; set; } = [];
        public IReadOnlyList<DisplayInfo> Enumerate() => Items;
    }

    private sealed class FakeBackend(GammaDeviceState initial) : IGammaCalibrationBackend
    {
        private int _reads;
        public int Writes { get; private set; }
        public GammaDeviceState State { get; set; } = initial;
        public Func<int, GammaDeviceState>? OnRead { get; set; }
        public GammaDeviceState Read(string displayId) => OnRead?.Invoke(++_reads) ?? State;
        public bool TryWrite(GammaDeviceState expected, GammaRamp ramp, Func<bool>? canWrite = null)
        {
            if (canWrite?.Invoke() == false) return false;
            Writes++;
            State = State with { Ramp = ramp.Copy() };
            return true;
        }
        public GammaProfileSelection Commit(GammaCalibrationSession session) => throw new InvalidOperationException("The restore must reuse the selected profile.");
        public bool RestoreProfile(GammaCalibrationSession session, GammaDeviceState expected) =>
            throw new InvalidOperationException("The restore must not change the profile.");
    }

    private sealed class Journal : IGammaCalibrationJournal
    {
        public List<GammaCalibrationSession> Sessions { get; } = [];
        public void Save(GammaCalibrationSession session) => Sessions.Add(session);
    }
}
