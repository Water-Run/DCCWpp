using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using Xunit;

namespace DccwPlus.Windows.Tests;

public sealed class CalibrationCompletionTests : IDisposable
{
    private static readonly string ProfileHash = new('A', 64);
    private static readonly string Key = "path:" + new string('B', 64);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dccwpp-completion-" + Guid.NewGuid().ToString("N"));
    private readonly FixtureDisplays _displays = new();
    private readonly GammaCalibrationStore _sessions;
    private readonly PresetStore _presets;
    private readonly CalibrationBindingStore _bindings;
    private readonly CalibrationCompletionService _service;
    private readonly GammaCalibrationSession _session;
    private GammaDeviceState _current;

    public CalibrationCompletionTests()
    {
        const string displayId = "0123456789abcdef", target = "00000000:00000001:1";
        _sessions = new(_root);
        _presets = new(_root);
        _bindings = new(_root);
        _displays.Items = [new(displayId, "Display", "SDR", true, "sdr", TargetKey: target,
            SourceId: 1, PersistenceKey: Key, PersistenceKind: "device-path")];
        var original = new GammaDeviceState(displayId, "SDR", target, new string('C', 64), true, false, GammaRamp.Identity(),
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
        _current = original with { ProfileName = _session.InstalledProfile, ProfileScope = "current-user",
            SelectedProfileScope = "current-user", Ramp = candidate };
        _service = new(_root, _displays, _ => _current, _ => ProfileHash);
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
        Assert.Equal("User renamed", _presets.Get(_session.Preset.Id).Name);
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

        _displays.Items = connected;
        _current = _current with { TopologyFingerprint = new string('D', 64) };
        Assert.Equal("saved", _service.CompleteLocked(_sessions.Load(_session.Id), replay: true).BindingStatus);
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
    [InlineData(false, "removed")]
    [InlineData(true, "unchanged")]
    public void UndoOnlyRemovesTheOldBindingWhileTheRestoredProfileRemainsSelected(bool reselected, string expected)
    {
        _sessions.Save(_session with { BindingStatus = "saved" });
        _bindings.Save(Existing(_session.InstalledProfile!));
        var undo = _session with { Id = Guid.NewGuid(), Original = _current, UndoOf = _session.Id,
            Candidate = _session.Original.Ramp, InstalledProfile = _session.Original.ProfileName, BindingIntent = null };
        if (!reselected) _current = _session.Original;

        var result = _service.CompleteLocked(undo, replay: true);

        Assert.Equal(expected, result.BindingStatus);
        Assert.Equal(reselected, _bindings.List().Count > 0);
    }

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
