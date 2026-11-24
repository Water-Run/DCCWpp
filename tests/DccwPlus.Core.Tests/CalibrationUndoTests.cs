using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using Xunit;

namespace DccwPlus.Core.Tests;

public sealed class CalibrationUndoTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelAndTimeoutReturnToConfirmedCalibrationWithoutChangingItsIcc(bool timeout)
    {
        var fixture = new Fixture();
        var preview = fixture.Begin();
        Assert.True(fixture.Backend.Current.Ramp.Matches(fixture.Saved.Original.Ramp));
        Assert.Equal(fixture.Saved.InstalledProfile, fixture.Backend.Current.ProfileName);
        Assert.Equal("undo_preview_fixed", Assert.Throws<ProfilePreviewException>(() => fixture.Engine.Update(fixture.Saved.Preset)).Code);

        fixture.To(TimeSpan.FromSeconds(20));
        fixture.Engine.Renew();
        fixture.Engine.SetReviewMode(false);
        Assert.Equal(preview.ExpiresAt, fixture.Engine.Session!.ExpiresAt);

        fixture.To(TimeSpan.FromSeconds(31));
        Assert.Equal("reverted", (timeout ? fixture.Engine.Tick() : fixture.Engine.Revert()).Status);
        Assert.True(fixture.Backend.Current.Ramp.Matches(fixture.Saved.Candidate));
        Assert.Equal(fixture.Saved.InstalledProfile, fixture.Backend.Current.ProfileName);
        Assert.Equal(0, fixture.Backend.ProfileWrites);
    }

    [Theory]
    [InlineData("factory.icc")]
    [InlineData(null)]
    public void ConfirmVerifiesThePreviousProfileAndCanRestoreNoExplicitDefault(string? previous)
    {
        var fixture = new Fixture(previous);
        fixture.Begin();
        var result = fixture.Engine.Confirm();
        Assert.Equal("confirmed", result.Status);
        Assert.Equal(fixture.Saved.Id, result.UndoOf);
        Assert.Equal(previous, result.InstalledProfile);
        Assert.Equal(previous, fixture.Backend.Current.ProfileName);
        Assert.True(fixture.Backend.Current.Ramp.Matches(fixture.Saved.Original.Ramp));
        Assert.Equal(1, fixture.Backend.ProfileWrites);
    }

    [Fact]
    public void FailedProfileSwitchRestoresTheConfirmedProfileAndCurve()
    {
        var fixture = new Fixture();
        fixture.Begin();
        fixture.Backend.FailAfterChangingProfile = true;
        Assert.Throws<IOException>(() => fixture.Engine.Confirm());
        Assert.Equal("reverted", fixture.Engine.Session!.Status);
        Assert.Equal(fixture.Saved.InstalledProfile, fixture.Backend.Current.ProfileName);
        Assert.True(fixture.Backend.Current.Ramp.Matches(fixture.Saved.Candidate));
        Assert.Equal(2, fixture.Backend.ProfileWrites);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("curve")]
    [InlineData("target")]
    public void ChangedProfileCurveOrTargetBlocksUndo(string changed)
    {
        var fixture = new Fixture();
        var before = fixture.Backend.Current;
        fixture.Backend.Current = changed switch
        {
            "profile" => before with { ProfileName = "other.icc" },
            "curve" => before with { Ramp = GammaRamp.Identity() },
            _ => before with { SourceId = 44 }
        };
        Assert.Equal($"undo_{changed}_changed", Assert.Throws<ProfilePreviewException>(fixture.Begin).Code);
        Assert.Equal(0, fixture.Backend.ProfileWrites);
        Assert.Null(fixture.Engine.Session);
    }

    [Fact]
    public void PersistedUndoRecoveryRestoresFromACommitInterruptedAfterTheProfileSwitch()
    {
        var fixture = new Fixture();
        var active = fixture.Begin();
        string root = Path.Combine(Path.GetTempPath(), "dccwpp-undo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new GammaCalibrationStore(root);
            store.Save(active with { Status = "committing", ProfileCommitStarted = true });
            fixture.Backend.Commit(active);
            var recovered = new GammaCalibrationTransaction(fixture.Backend, store).Recover(store.Load(active.Id));
            Assert.Equal("reverted", recovered.Status);
            Assert.Equal(fixture.Saved.Id, recovered.UndoOf);
            Assert.True(fixture.Backend.Current.Ramp.Matches(fixture.Saved.Candidate));
            Assert.Equal(fixture.Saved.InstalledProfile, fixture.Backend.Current.ProfileName);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class Fixture
    {
        private readonly Clock _clock = new();
        public GammaCalibrationSession Saved { get; }
        public Backend Backend { get; }
        public GammaCalibrationTransaction Engine { get; }
        public Fixture(string? previous = "factory.icc")
        {
            var state = new GammaDeviceState("0123456789abcdef", "display", "target", new string('A', 64), true, false,
                GammaRampMath.Compose(GammaRamp.Identity(), new() { Gamma = 2.1 }), previous, previous is null ? null : "system-wide", 1);
            var preset = CalibrationPreset.Create("Saved", new() { RedGain = .8 });
            var adjusted = GammaRampMath.Compose(state.Ramp, preset.Calibration);
            Saved = new()
            {
                Id = Guid.NewGuid(), Original = state, Preset = preset, Candidate = adjusted, LastWritten = adjusted,
                ExpiresAt = _clock.GetUtcNow(), StartedAt = _clock.GetUtcNow(), Status = "confirmed",
                InstalledProfile = "DCCWPlus-saved.icc",
            };
            Backend = new(Saved);
            Engine = new(Backend, new Journal(), _clock);
        }
        public GammaCalibrationSession Begin()
        {
            CalibrationUndoPolicy.VerifyCurrent(Saved, Backend.Current, Saved.Candidate);
            return Engine.Begin(Guid.NewGuid(), Saved.Preset, Saved.Original.DisplayId, 30,
                Saved.Original.Ramp, Backend.Current, Saved.Id);
        }
        public void To(TimeSpan elapsed) => _clock.Elapsed = elapsed;
    }

    private sealed class Journal : IGammaCalibrationJournal { public void Save(GammaCalibrationSession session) { } }

    private sealed class Clock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero) + Elapsed;
    }

    private sealed class Backend(GammaCalibrationSession saved) : IGammaCalibrationBackend
    {
        public GammaDeviceState Current { get; set; } = saved.Original with
        {
            ProfileName = saved.InstalledProfile, ProfileScope = "current-user", Ramp = saved.Candidate.Copy()
        };
        public int ProfileWrites { get; private set; }
        public bool FailAfterChangingProfile { get; set; }
        public GammaDeviceState Read(string displayId) => Current with { Ramp = Current.Ramp.Copy() };
        public void Write(GammaDeviceState expected, GammaRamp ramp) => Current = Current with { Ramp = ramp.Copy() };
        public string? Commit(GammaCalibrationSession session)
        {
            Current = Current with { ProfileName = saved.Original.ProfileName, ProfileScope = saved.Original.ProfileScope };
            ProfileWrites++;
            if (FailAfterChangingProfile) throw new IOException("Simulated fault after the ICC switch.");
            return Current.ProfileName;
        }
        public void RestoreProfile(GammaCalibrationSession session)
        {
            Current = Current with { ProfileName = session.Original.ProfileName, ProfileScope = session.Original.ProfileScope };
            ProfileWrites++;
        }
    }
}
