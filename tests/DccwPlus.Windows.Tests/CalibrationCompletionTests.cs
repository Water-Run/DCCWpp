using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using Xunit;

namespace DccwPlus.Windows.Tests;

public sealed class CalibrationCompletionTests : IDisposable
{
    private static readonly string ProfileHash = new('A', 64);
    private static readonly string Key = "path:" + new string('B', 64);
    private const string DeviceKey = @"\Registry\Machine\System\Fixture";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dccwpp-completion-" + Guid.NewGuid().ToString("N"));
    private readonly FixtureDisplays _displays = new();
    private readonly GammaCalibrationStore _sessions;
    private readonly PresetStore _presets;
    private readonly CalibrationBindingStore _bindings;
    private readonly CalibrationCompletionService _service;
    private readonly GammaCalibrationSession _session;
    private Func<string, string> _readHash = _ => ProfileHash;

    public CalibrationCompletionTests()
    {
        const string displayId = "0123456789abcdef", target = "00000000:00000001:1";
        _sessions = new(_root);
        _presets = new(_root);
        _bindings = new(_root);
        _displays.Items = [new(displayId, "Display", "SDR", true, "sdr", TargetKey: target,
            SourceId: 1, ProfileDeviceKey: DeviceKey, PersistenceKey: Key, PersistenceKind: "device-path",
            HdrActive: false, AdvancedColorActive: false, ProfileQueryStatus: "read")];
        var original = new GammaDeviceState(displayId, "SDR", target, WindowsGammaCalibrationBackend.ReadTopologyFingerprint(_displays.Items),
            true, false, GammaRamp.Identity(),
            "previous.icc", "system-wide", 1, Key, SelectedProfileScope: "system-wide");
        var preset = CalibrationPreset.Create("Completed calibration", new() { RedGain = .9 });
        Guid id = Guid.NewGuid();
        var candidate = GammaRampMath.Compose(original.Ramp, preset.Calibration);
        _session = new()
        {
            Id = id, Original = original, Preset = preset, Candidate = candidate, LastWritten = candidate,
            StartedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30), Status = "confirmed",
            ProfileCommitStarted = true, CurveReadbackVerified = true, InstalledProfile = $"DCCWPlus-{id:N}.icc",
            BindingStatus = "pending", BindingIntent = new(null)
        };
        _displays.Items = [_displays.Items[0] with { ProfileName = _session.InstalledProfile, ProfileScope = "current-user",
            SelectedProfileScope = "current-user" }];
        _service = new(_root, _displays, saved => new(0, null, saved.Original.SourceId!.Value, saved.Original.TargetKey,
            saved.InstalledProfile!, DeviceKey, saved.Original.ProfileName, ProfileHash, CandidateHash: ProfileHash),
            name => _readHash(name), () => { });
    }

    [Fact]
    public void ReopensThePersistedIntentAndCompletesBothLocalRecords()
    {
        _sessions.Save(_session);
        Assert.Equal("binding", Assert.Single(new PreviewRecoveryService(_root).List()).Kind);

        var completed = _service.CompleteLocked(_sessions.Load(_session.Id), replay: true);

        Assert.Equal("confirmed", completed.Status);
        Assert.Equal("saved", _sessions.Load(_session.Id).BindingStatus);
        Assert.Equal(_session.Preset, _presets.Get(_session.Preset.Id));
        var binding = Assert.Single(_bindings.List());
        Assert.Equal(_session.InstalledProfile, binding.ProfileName);
        Assert.Equal(WindowsDisplayService.HashProfileDeviceKey(DeviceKey), binding.ProfileDeviceSha256);
        Assert.True(binding.Ramp.Matches(_session.Candidate));
        Assert.False(binding.RestoreEnabled);
        Assert.Empty(new PreviewRecoveryService(_root).List());
    }

    [Fact]
    public void ReplayAfterTheBindingWasWrittenPreservesItsTimestampAndOptIn()
    {
        var binding = Existing(_session.InstalledProfile!) with { RestoreEnabled = true };
        _bindings.Save(binding);
        _presets.Add(_session.Preset with { Name = "User renamed" });

        Assert.Equal("saved", _service.CompleteLocked(_session, replay: true).BindingStatus);
        var actual = Assert.Single(_bindings.List());
        Assert.Equal(binding.SavedAt, actual.SavedAt);
        Assert.True(actual.RestoreEnabled);
        Assert.Equal(WindowsDisplayService.HashProfileDeviceKey(DeviceKey), actual.ProfileDeviceSha256);
        Assert.Equal("User renamed", _presets.Get(_session.Preset.Id).Name);
    }

    [Theory]
    [InlineData("same-device", true)]
    [InlineData("other-device", false)]
    [InlineData("legacy-with-journal", true)]
    [InlineData("legacy-with-different-icc", false)]
    [InlineData("legacy-without-journal", false)]
    public void ReplacementOnlyInheritsOptInFromTheVerifiedPreviousDevice(string evidence, bool keepOptIn)
    {
        var previousId = Guid.NewGuid();
        string previousName = $"DCCWPlus-{previousId:N}.icc";
        if (evidence is "legacy-with-journal" or "legacy-with-different-icc")
            _sessions.Save(_session with { Id = previousId, InstalledProfile = previousName, BindingStatus = "saved" });
        _bindings.Save(Existing(previousName) with
        {
            ProfileDeviceSha256 = evidence switch
            {
                "same-device" => WindowsDisplayService.HashProfileDeviceKey(DeviceKey),
                "other-device" => WindowsDisplayService.HashProfileDeviceKey(DeviceKey + "-other"),
                _ => null
            },
            ProfileSha256 = evidence == "legacy-with-different-icc" ? new string('D', 64) : ProfileHash,
            RestoreEnabled = true
        });
        var session = _session with { BindingIntent = _service.CaptureIntent(_session) };

        Assert.Equal("saved", _service.CompleteLocked(session, replay: true).BindingStatus);
        var binding = Assert.Single(_bindings.List());
        Assert.Equal(_session.InstalledProfile, binding.ProfileName);
        Assert.Equal(keepOptIn, binding.RestoreEnabled);
    }

    [Fact]
    public void CompletingUndoKeepsABindingThatRecordsADifferentProfileDevice()
    {
        _sessions.Save(_session with { BindingStatus = "saved" });
        _bindings.Save(Existing(_session.InstalledProfile!) with
        {
            ProfileDeviceSha256 = WindowsDisplayService.HashProfileDeviceKey(DeviceKey + "-other"), RestoreEnabled = true
        });
        var beforeUndo = _session.Original with { ProfileName = _session.InstalledProfile, ProfileScope = "current-user", SelectedProfileScope = "current-user" };
        var undo = _session with { Id = Guid.NewGuid(), Original = beforeUndo, UndoOf = _session.Id,
            Candidate = _session.Original.Ramp, InstalledProfile = _session.Original.ProfileName, BindingIntent = null };

        var result = _service.CompleteLocked(undo, replay: false);

        Assert.Equal("unchanged", result.BindingStatus);
        Assert.Equal("binding_changed", result.BindingReasonCode);
        Assert.True(Assert.Single(_bindings.List()).RestoreEnabled);
    }

    [Fact]
    public void ThePreviousBindingIsOnlyReplacedWhenItStillMatchesTheRecordedIntent()
    {
        _bindings.Save(Existing("DCCWPlus-previous.icc") with { RestoreEnabled = true });
        var captured = _session with { BindingIntent = _service.CaptureIntent(_session) };
        _bindings.Save(Existing("DCCWPlus-newer.icc"));

        var result = _service.CompleteLocked(captured, replay: true);

        Assert.Equal("unchanged", result.BindingStatus);
        Assert.Equal("binding_changed", result.BindingReasonCode);
        Assert.Equal("DCCWPlus-newer.icc", Assert.Single(_bindings.List()).ProfileName);
        Assert.Empty(_presets.List().Presets);
    }

    [Fact]
    public void DisconnectedDisplayCanCompleteAfterReconnectingOnTheSamePath()
    {
        var connected = _displays.Items;
        _displays.Items = [];
        var pending = _service.CompleteLocked(_session, replay: true);
        Assert.True(pending.NeedsBindingCompletion);
        Assert.Equal("display_disconnected", pending.BindingReasonCode);
        Assert.Empty(_bindings.List());

        _displays.Items = [connected[0] with { PositionX = 300 }];
        Assert.Equal("saved", _service.CompleteLocked(_sessions.Load(_session.Id), replay: true).BindingStatus);
    }

    [Fact]
    public void RefreshRateChangeAfterCommitDefersTheBindingUntilReplay()
    {
        var display = _displays.Items[0] with
        {
            Topology = "internal", Width = 1920, Height = 1080, PositionX = 0, PositionY = 0, RefreshMillihertz = 60_000
        };
        var session = _session with
        {
            Original = _session.Original with
            {
                TopologyFingerprint = WindowsGammaCalibrationBackend.ReadTopologyFingerprint([display]),
                ActiveTopologySha256 = WindowsDisplayService.ComputeTopologyHash(display.Topology, [display])
            }
        };
        Assert.NotNull(session.Original.ActiveTopologySha256);
        _displays.Items = [display with { RefreshMillihertz = 120_000 }];

        var pending = _service.CompleteLocked(session, replay: false);

        Assert.True(pending.NeedsBindingCompletion);
        Assert.Equal("display_state_changed", pending.BindingReasonCode);
        Assert.Empty(_bindings.List());
        var completed = _service.CompleteLocked(_sessions.Load(session.Id), replay: true);
        Assert.Equal("saved", completed.BindingStatus);
    }

    [Fact]
    public void ReplayCanFinishAfterWindowsReassignsTheAdapterAndSource()
    {
        var oldPath = _displays.Items[0];
        _displays.Items = [oldPath with { PersistenceKey = "path:" + new string('F', 64), ProfileName = "other.icc" }, Reconnected(oldPath)];
        _sessions.Save(_session);

        var recovered = _service.CompleteLocked(_sessions.Load(_session.Id), replay: true);

        Assert.Equal("saved", recovered.BindingStatus);
        Assert.Equal(Key, Assert.Single(_bindings.List()).PersistenceKey);
        Assert.Equal(_session.Preset, _presets.Get(_session.Preset.Id));
    }

    [Fact]
    public void InitialCompletionDefersIfTheDisplayChangesAfterConfirmation()
    {
        _displays.Items = [Reconnected(_displays.Items[0])];

        var pending = _service.CompleteLocked(_session, replay: false);

        Assert.Equal("pending", pending.BindingStatus);
        Assert.Equal("display_state_changed", pending.BindingReasonCode);
        Assert.Equal(_session.Preset, _presets.Get(_session.Preset.Id));
        Assert.Empty(_bindings.List());
        Assert.Equal("saved", _service.CompleteLocked(pending, replay: true).BindingStatus);
    }

    [Theory]
    [InlineData("different-device", "pending", "profile_device_unavailable")]
    [InlineData("duplicate-identity", "pending", "display_identity_ambiguous")]
    [InlineData("wrong-scope", "failed", "profile_selection_changed")]
    [InlineData("changed-file", "failed", "profile_file_changed")]
    [InlineData("racing-selection", "pending", "display_state_changed")]
    [InlineData("hdr", "pending", "sdr_path_blocked")]
    public void ReconnectingDoesNotCompleteWithoutTheMatchingProfileAndDevice(string change, string status, string reason)
    {
        var active = Reconnected(_displays.Items[0]);
        _displays.Items = change switch
        {
            "different-device" => [active with { ProfileDeviceKey = DeviceKey + "2" }],
            "duplicate-identity" => [active, _displays.Items[0]],
            "wrong-scope" => [active with { SelectedProfileScope = "system-wide" }],
            "hdr" => [active with { HdrActive = true, ColorState = "hdr" }],
            _ => [active]
        };
        _readHash = _ =>
        {
            if (change == "racing-selection") _displays.Items = [active with { ProfileName = "other.icc" }];
            return change == "changed-file" ? new string('D', 64) : ProfileHash;
        };

        var result = _service.CompleteLocked(_session, replay: true);

        Assert.Equal(status, result.BindingStatus);
        Assert.Equal(reason, result.BindingReasonCode);
        Assert.Empty(_bindings.List());
        Assert.Empty(_presets.List().Presets);
    }

    [Fact]
    public void LegacyPendingJournalDoesNotCreateABindingWithoutAnIntent()
    {
        var result = _service.CompleteLocked(_session with { BindingIntent = null }, replay: true);
        Assert.Equal("unchanged", result.BindingStatus);
        Assert.Equal("binding_intent_unavailable", result.BindingReasonCode);
        Assert.Empty(_bindings.List());
    }

    [Theory]
    [InlineData("restored", "removed", null)]
    [InlineData("no-default", "removed", null)]
    [InlineData("reselected", "unchanged", "profile_selection_changed")]
    [InlineData("file-changed", "failed", "profile_file_changed")]
    public void UndoReplayVerifiesTheOldProfileAfterWindowsReassignsThePath(string change, string expected, string? reason)
    {
        var source = change == "no-default" ? _session with
        {
            Original = _session.Original with { ProfileName = null, ProfileScope = null }
        } : _session;
        _sessions.Save(source with { BindingStatus = "saved" });
        _bindings.Save(Existing(_session.InstalledProfile!));
        var beforeUndo = source.Original with { ProfileName = source.InstalledProfile, ProfileScope = "current-user", SelectedProfileScope = "current-user" };
        var undo = _session with { Id = Guid.NewGuid(), Original = beforeUndo, UndoOf = _session.Id,
            Candidate = source.Original.Ramp, InstalledProfile = source.Original.ProfileName, BindingIntent = null };
        var active = Reconnected(_displays.Items[0]);
        _displays.Items = [change == "reselected" ? active : active with { ProfileName = source.Original.ProfileName,
            ProfileScope = source.Original.ProfileScope, SelectedProfileScope = "system-wide",
            ProfileQueryStatus = change == "no-default" ? "not-found" : "read" }];
        if (change == "file-changed") _readHash = _ => new string('F', 64);
        if (change == "no-default") _readHash = _ => throw new InvalidOperationException("There is no ICC file to read.");

        var result = _service.CompleteLocked(undo, replay: true);

        Assert.Equal(expected, result.BindingStatus);
        Assert.Equal(reason, result.BindingReasonCode);
        Assert.Equal(expected != "removed", _bindings.List().Count > 0);
    }

    private static DisplayInfo Reconnected(DisplayInfo display) => display with
    {
        Id = "abcdef0123456789", DeviceName = "SDR2", TargetKey = "00000000:00000002:4", SourceId = 9
    };

    private CalibrationBinding Existing(string profile) => new()
    {
        PersistenceKey = Key, IdentityKind = "device-path", DisplayName = "Display", PresetId = _session.Preset.Id,
        ProfileName = profile, ProfileSha256 = ProfileHash, Ramp = _session.Candidate, OriginalRamp = _session.Original.Ramp,
        SavedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    public void Dispose()
    { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private sealed class FixtureDisplays : IDisplayService
    {
        public IReadOnlyList<DisplayInfo> Items { get; set; } = [];
        public IReadOnlyList<DisplayInfo> Enumerate() => Items;
    }
}
