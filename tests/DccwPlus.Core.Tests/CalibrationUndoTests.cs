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
    [InlineData("factory.icc", "system-wide")]
    [InlineData("factory.icc", "current-user")]
    [InlineData(null, "system-wide")]
    [InlineData(null, "current-user")]
    public void ConfirmVerifiesThePreviousProfileAndCanRestoreNoExplicitDefault(string? previous, string selectedScope)
    {
        var fixture = new Fixture(previous, selectedScope: selectedScope);
        fixture.Begin();
        var result = fixture.Engine.Confirm();
        Assert.Equal("confirmed", result.Status);
        Assert.Equal(fixture.Saved.Id, result.UndoOf);
        Assert.Equal(previous, result.InstalledProfile);
        Assert.Equal(previous, fixture.Backend.Current.ProfileName);
        Assert.Equal(previous is null ? null : "system-wide", fixture.Backend.Current.ProfileScope);
        Assert.Equal(selectedScope, fixture.Backend.Current.SelectedProfileScope);
        Assert.True(fixture.Backend.Current.Ramp.Matches(fixture.Saved.Original.Ramp));
        Assert.Equal(1, fixture.Backend.ProfileWrites);
    }

    [Theory]
    [InlineData("factory.icc")]
    [InlineData(null)]
    public void UndoCannotConfirmAfterTheSelectedScopeChanges(string? previous)
    {
        var fixture = new Fixture(previous);
        fixture.Begin();
        fixture.Backend.AfterCommit = () => fixture.Backend.Current = fixture.Backend.Current with { SelectedProfileScope = "current-user" };

        var error = Assert.Throws<ProfilePreviewException>(() => fixture.Engine.Confirm());

        Assert.Equal("profile_selection_changed", error.Code);
        Assert.Equal("reverted", fixture.Engine.Session!.Status);
        Assert.Equal(error.Code, fixture.Engine.Session.ReasonCode);
        Assert.False(fixture.Engine.Session.NeedsBindingCompletion);
        Assert.Equal(fixture.Saved.InstalledProfile, fixture.Backend.Current.ProfileName);
        Assert.True(fixture.Backend.Current.Ramp.Matches(fixture.Saved.Candidate));
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
    [InlineData(false)]
    [InlineData(true)]
    public void UndoExpiryDuringConfirmationKeepsTheSavedProfileAndCurve(bool afterProfileSwitch)
    {
        var fixture = new Fixture();
        fixture.Begin();
        if (afterProfileSwitch) fixture.Backend.AfterCommit = Expire;
        else fixture.Backend.BeforeCommit = Expire;

        var result = fixture.Engine.Confirm();

        Assert.Equal("reverted", result.Status);
        Assert.Equal("preview_expired", result.ReasonCode);
        Assert.Equal(afterProfileSwitch ? 2 : 0, fixture.Backend.ProfileWrites);
        Assert.Equal(fixture.Saved.InstalledProfile, fixture.Backend.Current.ProfileName);
        Assert.Equal("current-user", fixture.Backend.Current.SelectedProfileScope);
        Assert.True(fixture.Backend.Current.Ramp.Matches(fixture.Saved.Candidate));

        void Expire() => fixture.To(TimeSpan.FromSeconds(31));
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
    public void UndoStartsOnTheCurrentPathOfTheSavedDisplay()
    {
        string identity = "edid:" + new string('B', 64);
        var fixture = new Fixture(identity: identity);
        fixture.Backend.Current = fixture.Backend.Current with
        {
            DisplayId = "fedcba9876543210", DeviceName = "display2", TargetKey = "new-target", SourceId = 4
        };

        var preview = fixture.Begin();
        Assert.Equal(fixture.Backend.Current.DisplayId, preview.Original.DisplayId);
        Assert.Equal(fixture.Saved.Id, preview.UndoOf);
        Assert.Equal("confirmed", fixture.Engine.Confirm().Status);
        Assert.Equal(fixture.Saved.Original.ProfileName, fixture.Backend.Current.ProfileName);
        Assert.True(fixture.Backend.Current.Ramp.Matches(fixture.Saved.Original.Ramp));
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
            fixture.Backend.Commit(active, _ => { });
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
        public Fixture(string? previous = "factory.icc", string? identity = null, string selectedScope = "system-wide")
        {
            var state = new GammaDeviceState("0123456789abcdef", "display", "target", new string('A', 64), true, false,
                GammaRampMath.Compose(GammaRamp.Identity(), new() { Gamma = 2.1 }), previous, previous is null ? null : "system-wide",
                1, identity, SelectedProfileScope: selectedScope);
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
            return Engine.Begin(Guid.NewGuid(), Saved.Preset, Backend.Current.DisplayId, 30,
                Saved.Original.Ramp, Backend.Current, Saved.Id);
        }
        public void To(TimeSpan elapsed) => _clock.Elapsed = elapsed;
    }

    private sealed class Journal : IGammaCalibrationJournal
    {
        public void Save(GammaCalibrationSession session, Action? beforePublish = null) => beforePublish?.Invoke();
    }

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
            ProfileName = saved.InstalledProfile, ProfileScope = "current-user", SelectedProfileScope = "current-user", Ramp = saved.Candidate.Copy()
        };
        public int ProfileWrites { get; private set; }
        public bool FailAfterChangingProfile { get; set; }
        public Action? BeforeCommit { get; set; }
        public Action? AfterCommit { get; set; }
        public GammaDeviceState Read(string displayId) => Current with { Ramp = Current.Ramp.Copy() };
        public bool TryWrite(GammaDeviceState expected, GammaRamp ramp, Func<bool>? canWrite = null)
        {
            if (canWrite?.Invoke() == false) return false;
            Current = Current with { Ramp = ramp.Copy() };
            return true;
        }
        public GammaProfileSelection Commit(GammaCalibrationSession session, AuthorizeCalibrationCommit authorize)
        {
            BeforeCommit?.Invoke();
            authorize(changesProfileSelection: true);
            Current = Current with { ProfileName = saved.Original.ProfileName, ProfileScope = saved.Original.ProfileScope,
                SelectedProfileScope = saved.Original.SelectedProfileScope };
            ProfileWrites++;
            if (FailAfterChangingProfile) throw new IOException("Simulated fault after the ICC switch.");
            AfterCommit?.Invoke();
            return new(saved.Original.ProfileName, saved.Original.ProfileScope, saved.Original.SelectedProfileScope);
        }
        public bool RestoreProfile(GammaCalibrationSession session, GammaDeviceState expected, Action? beforeSelectionRestore = null)
        {
            bool changed = !GammaCalibrationTransaction.SameProfile(session.Original, Current);
            if (changed) beforeSelectionRestore?.Invoke();
            Current = Current with { ProfileName = session.Original.ProfileName, ProfileScope = session.Original.ProfileScope,
                SelectedProfileScope = session.Original.SelectedProfileScope };
            ProfileWrites++;
            return changed;
        }
    }
}
