using System.Buffers.Binary;
using System.Text;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using Xunit;

namespace DccwPlus.Core.Tests;

public sealed class GammaCalibrationTests
{
    [Fact] public void ScopeChangeSupersedesPreviewEvenWithTheSameEffectiveProfile()
    {
        var fixture = new Fixture();
        fixture.Backend.State = fixture.Backend.State with
        { ProfileName = "panel.icc", ProfileScope = "system-wide", SelectedProfileScope = "system-wide" };
        fixture.Begin();
        fixture.Backend.State = fixture.Backend.State with { SelectedProfileScope = "current-user" };
        fixture.Backend.BeforeWrite = () => throw new InvalidOperationException("No writes after the scope changed.");
        var result = fixture.Engine.Tick();
        Assert.Equal("superseded", result.Status);
        Assert.Equal("external_profile_change", result.ReasonCode);
    }

    [Fact] public void RecoveryWaitsForTheProfileStateToBecomeReadable()
    {
        var fixture = new Fixture();
        fixture.Begin();
        fixture.Backend.BeforeRead = () => throw new ProfilePreviewException("profile_state_unavailable", "Simulated lookup failure.");
        var pending = fixture.Engine.Revert();
        Assert.Equal("recovery-pending", pending.Status);
        Assert.Equal("profile_state_unavailable", pending.ReasonCode);
        fixture.Backend.BeforeRead = null;
        var recovered = new GammaCalibrationTransaction(fixture.Backend, fixture.Journal).Recover(pending);
        Assert.Equal("reverted", recovered.Status);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
    }

    [Fact] public void PreparedOnlyRecoveryDoesNotNeedTheDisconnectedDisplay()
    {
        var fixture = new Fixture();
        GammaCalibrationSession? prepared = null;
        fixture.Journal.BeforeSave = session =>
        {
            if (session.Status != "prepared") return;
            prepared = session;
            throw new IOException("Simulated interruption after journaling.");
        };
        Assert.Throws<IOException>(() => fixture.Begin());
        fixture.Backend.Disconnected = true;
        int reads = 0;
        fixture.Backend.BeforeRead = () => reads++;

        var resolved = new GammaCalibrationTransaction(fixture.Backend, fixture.Journal).Recover(Assert.IsType<GammaCalibrationSession>(prepared));

        Assert.Equal(0, reads);
        Assert.Equal("reverted", resolved.Status);
        Assert.Equal("preview_not_applied", resolved.ReasonCode);
        Assert.False(resolved.CurveReadbackVerified);
        Assert.False(fixture.Backend.ProfileRestoreCalled);
    }

    [Theory]
    [InlineData("profile_file_changed")]
    [InlineData("profile_file_unavailable")]
    [InlineData("previous_system_profile_changed")]
    public void InterruptedCommitWaitsForTheRecordedProfileBeforeRewritingTheCurve(string reason)
    {
        var fixture = new Fixture();
        var interrupted = fixture.Begin() with { Status = "committing", ProfileCommitStarted = true };
        fixture.Backend.BeforeRestoreProfile = () => throw new ProfilePreviewException(reason, "Simulated ICC verification failure.");

        var pending = new GammaCalibrationTransaction(fixture.Backend, fixture.Journal).Recover(interrupted);

        Assert.Equal("recovery-pending", pending.Status);
        Assert.Equal(reason, pending.ReasonCode);
        Assert.True(fixture.Backend.State.Ramp.Matches(interrupted.Candidate));

        fixture.Backend.BeforeRestoreProfile = null;
        var recovered = new GammaCalibrationTransaction(fixture.Backend, fixture.Journal).Recover(pending);
        Assert.Equal("reverted", recovered.Status);
        Assert.True(fixture.Backend.State.Ramp.Matches(interrupted.Original.Ramp));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IccRollbackCanRestoreANonlinearOriginalAfterALinearReset(bool resetBeforeRollback, bool failFirstWrite)
    {
        var fixture = new Fixture();
        var interrupted = BeginInterruptedCommit(fixture);
        if (resetBeforeRollback) fixture.Backend.State = fixture.Backend.State with { Ramp = GammaRamp.Identity() };
        fixture.Backend.RestoredProfileRamp = GammaRamp.Identity();
        fixture.Backend.FailNextWrite = failFirstWrite;

        var recovered = new GammaCalibrationTransaction(fixture.Backend, fixture.Journal).Recover(interrupted);
        if (failFirstWrite)
        {
            Assert.Equal("recovery-pending", recovered.Status);
            Assert.True(recovered.BeforeWrite?.Matches(GammaRamp.Identity()));
            recovered = new GammaCalibrationTransaction(fixture.Backend, fixture.Journal).Recover(recovered);
        }

        Assert.Equal("reverted", recovered.Status);
        Assert.True(recovered.CurveReadbackVerified);
        Assert.True(fixture.Backend.State.Ramp.Matches(interrupted.Original.Ramp));
        Assert.True(GammaCalibrationTransaction.SameProfile(interrupted.Original, fixture.Backend.State));
    }

    [Theory]
    [InlineData("same-path", "reverted")]
    [InlineData("different-topology", "superseded")]
    [InlineData("external-curve", "superseded")]
    public void IccRollbackRechecksItsRecordedLinearResetAfterTheReadbackIsInterrupted(string condition, string expected)
    {
        string root = Directory.CreateTempSubdirectory("dccwpp-icc-rollback-").FullName;
        try { VerifyInterruptedIccRollback(root, condition, expected); }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifyInterruptedIccRollback(string root, string condition, string expected)
    {
        var fixture = new Fixture();
        var interrupted = BeginInterruptedCommit(fixture);
        var store = new GammaCalibrationStore(root);
        GammaCalibrationSession? beforeReadback = null;
        fixture.Backend.RestoredProfileRamp = GammaRamp.Identity();
        fixture.Backend.AfterRestoreProfile = () =>
        {
            beforeReadback = store.Load(interrupted.Id);
            fixture.Backend.BeforeRead = () => throw new IOException("Simulated lost readback immediately after the ICC switch.");
        };
        store.Save(interrupted);
        var pending = new GammaCalibrationTransaction(fixture.Backend, store).Recover(interrupted);
        Assert.Equal("recovery-pending", pending.Status);
        Assert.True(GammaCalibrationTransaction.SameProfile(interrupted.Original, fixture.Backend.State));
        var saved = Assert.IsType<GammaCalibrationSession>(beforeReadback);
        Assert.True(saved.ProfileRollbackMayResetCurve);
        Assert.Equal("recovering", saved.Status);

        fixture.Backend.AfterRestoreProfile = null;
        fixture.Backend.BeforeRead = null;
        if (condition == "different-topology")
            fixture.Backend.State = fixture.Backend.State with { TopologyFingerprint = new string('B', 64) };
        if (condition == "external-curve")
            fixture.Backend.State = fixture.Backend.State with
            { Ramp = GammaRampMath.Compose(GammaRamp.Identity(), new() { BlueGain = .8 }) };
        var observedRamp = fixture.Backend.State.Ramp;

        var recovered = new GammaCalibrationTransaction(fixture.Backend, store).Recover(saved);

        Assert.Equal(expected, recovered.Status);
        Assert.Equal(expected == "reverted", recovered.CurveReadbackVerified);
        Assert.True(fixture.Backend.State.Ramp.Matches(expected == "reverted" ? interrupted.Original.Ramp : observedRamp));
    }

    [Fact]
    public void IccRollbackWaitsWhenItCannotRecordThePossibleLinearReset()
    {
        var fixture = new Fixture();
        var interrupted = BeginInterruptedCommit(fixture);
        var before = fixture.Backend.State;
        fixture.Journal.BeforeSave = session =>
        {
            if (session.ProfileRollbackMayResetCurve) throw new IOException("Could not save before the ICC switch.");
        };

        var pending = new GammaCalibrationTransaction(fixture.Backend, fixture.Journal).Recover(interrupted);

        Assert.Equal("recovery-pending", pending.Status);
        Assert.False(pending.ProfileRollbackMayResetCurve);
        Assert.Equal(before, fixture.Backend.State);
    }

    [Theory]
    [InlineData("recovery_target_changed", false, "superseded")]
    [InlineData("previous_system_profile_changed", false, "superseded")]
    [InlineData("profile_device_unavailable", false, "superseded")]
    [InlineData("recovery_target_changed", true, "reverted")]
    [InlineData("profile_device_unavailable", true, "reverted")]
    public void ARejectedIccRollbackRetainsOnlyEvidenceFromAnEarlierAttempt(string reason, bool earlierIntent, string expected)
    {
        var fixture = new Fixture();
        var interrupted = BeginInterruptedCommit(fixture) with { ProfileRollbackMayResetCurve = earlierIntent };
        bool observedIntent = false;
        fixture.Journal.BeforeSave = session => observedIntent |= session.ProfileRollbackMayResetCurve;
        fixture.Backend.BeforeChangingProfile = () => throw new ProfilePreviewException(reason, "Simulated final write refusal.");

        var pending = new GammaCalibrationTransaction(fixture.Backend, fixture.Journal).Recover(interrupted);

        Assert.True(observedIntent);
        Assert.Equal("recovery-pending", pending.Status);
        Assert.Equal(reason, pending.ReasonCode);
        Assert.Equal(earlierIntent, pending.ProfileRollbackMayResetCurve);

        fixture.Backend.BeforeChangingProfile = null;
        fixture.Backend.State = interrupted.Original with { Ramp = GammaRamp.Identity() };
        var recovered = new GammaCalibrationTransaction(fixture.Backend, fixture.Journal).Recover(pending);
        Assert.Equal(expected, recovered.Status);
        Assert.True(fixture.Backend.State.Ramp.Matches(earlierIntent ? interrupted.Original.Ramp : GammaRamp.Identity()));
    }

    [Theory]
    [InlineData("external-before")]
    [InlineData("external-during")]
    [InlineData("unknown-reload")]
    [InlineData("no-profile-switch")]
    [InlineData("topology-changed")]
    [InlineData("linear-without-switch")]
    [InlineData("linear-after-topology-change")]
    [InlineData("linear-before-unknown-reload")]
    public void IccRollbackPreservesCurvesWhenTheTransitionIsUnverified(string condition)
    {
        var fixture = new Fixture();
        var interrupted = BeginInterruptedCommit(fixture);
        var external = GammaRampMath.Compose(GammaRamp.Identity(), new() { GreenGain = .65 });
        bool unknownReload = condition is "unknown-reload" or "linear-before-unknown-reload";
        fixture.Backend.RestoredProfileRamp = unknownReload ? external : GammaRamp.Identity();
        switch (condition)
        {
            case "external-before":
                fixture.Backend.State = fixture.Backend.State with { Ramp = external };
                break;
            case "external-during":
                fixture.Backend.BeforeRestoreProfile = () => fixture.Backend.State = fixture.Backend.State with { Ramp = external };
                break;
            case "no-profile-switch":
                fixture.Backend.State = interrupted.Original with { Ramp = interrupted.Candidate };
                fixture.Backend.AfterRestoreProfile = () => fixture.Backend.State = fixture.Backend.State with { Ramp = GammaRamp.Identity() };
                break;
            case "topology-changed":
                fixture.Backend.AfterRestoreProfile = () => fixture.Backend.State = fixture.Backend.State with { TopologyFingerprint = new string('B', 64) };
                break;
            case "linear-without-switch":
                fixture.Backend.State = interrupted.Original with { Ramp = GammaRamp.Identity() };
                break;
            case "linear-after-topology-change":
                fixture.Backend.State = fixture.Backend.State with { Ramp = GammaRamp.Identity(), TopologyFingerprint = new string('B', 64) };
                break;
            case "linear-before-unknown-reload":
                fixture.Backend.State = fixture.Backend.State with { Ramp = GammaRamp.Identity() };
                break;
        }

        var recovered = new GammaCalibrationTransaction(fixture.Backend, fixture.Journal).Recover(interrupted);

        Assert.Equal("superseded", recovered.Status);
        Assert.Equal("external_curve_change", recovered.ReasonCode);
        Assert.False(recovered.CurveReadbackVerified);
        Assert.True(fixture.Backend.State.Ramp.Matches(unknownReload ? external : GammaRamp.Identity()));
    }

    private static GammaCalibrationSession BeginInterruptedCommit(Fixture fixture)
    {
        fixture.Backend.State = fixture.Backend.State with
        {
            ProfileName = "previous.icc", ProfileScope = "system-wide", SelectedProfileScope = "system-wide",
            Ramp = GammaRampMath.Compose(GammaRamp.Identity(), new() { Gamma = 1.8 })
        };
        var interrupted = fixture.Begin() with { Status = "committing", ProfileCommitStarted = true };
        fixture.Backend.Commit(interrupted);
        return interrupted;
    }

    [Fact] public void Mhc2BaseStopsPreviewBeforeTheFirstWrite()
    {
        var fixture = new Fixture();
        fixture.Backend.State = fixture.Backend.State with { ProfileMhc2Present = true };
        var error = Assert.Throws<ProfilePreviewException>(() => fixture.Begin());
        Assert.Equal("mhc2_profile_blocked", error.Code);
        Assert.Null(fixture.Engine.Session);
        Assert.Null(fixture.Journal.LastApplying);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
    }

    [Fact] public void NeutralAdjustmentPreservesCapturedCurveExactly()
    {
        var original = GammaRamp.Identity();
        for (int i = 0; i < 256; i++) original.Red[i] = (ushort)Math.Round(Math.Pow(i / 255d, 1.1) * 65535);
        Assert.True(original.Matches(GammaRampMath.Compose(original, new())));
    }
    [Fact] public void GainsAndGammaComposeWithoutAccumulatingPreviousPreview()
    {
        var fixture = new Fixture(); fixture.Begin();
        var lower = fixture.Preset with { Calibration = new() { RedGain = .75, Gamma = 2.6 } };
        fixture.Engine.Update(lower);
        fixture.Engine.Update(fixture.Preset with { Calibration = new() });
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
        Assert.Equal((ushort)0, GammaRampMath.Compose(GammaRamp.Identity(), lower.Calibration).Red[0]);
        Assert.InRange(GammaRampMath.Compose(GammaRamp.Identity(), lower.Calibration).Red[255], (ushort)49150, (ushort)49152);
    }
    [Fact] public void PreviousAndCurrentComparisonDoesNotLoseCalibrationIntent()
    {
        var fixture = new Fixture(); fixture.Begin();
        var edited = fixture.Preset with { Calibration = new() { BlueGain = .8 } };
        fixture.Engine.Update(edited); fixture.Engine.Update(edited, showOriginal: true);
        Assert.True(fixture.Backend.State.Ramp.Matches(fixture.Engine.Session!.Original.Ramp));
        Assert.Equal("confirmed", fixture.Engine.Confirm().Status);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRampMath.Compose(GammaRamp.Identity(), edited.Calibration)));
        Assert.True(fixture.Backend.Committed);
    }

    [Fact] public void BindingIntentIsJournaledBeforeCommittingTheProfile()
    {
        var fixture = new Fixture();
        fixture.Begin();
        var intent = new BindingSaveIntent("DCCWPlus-previous.icc");
        fixture.Backend.BeforeCommit = () => Assert.Equal(intent, fixture.Journal.LastCommitting?.BindingIntent);

        var confirmed = fixture.Engine.Confirm(intent);

        Assert.True(confirmed.NeedsBindingCompletion);
        Assert.Equal(intent, confirmed.BindingIntent);
    }
    [Fact] public void TimeoutsAndOwnerExitRestoreOriginalWithoutSavingIcc()
    {
        var fixture = new Fixture(); fixture.Begin(); fixture.Clock.Advance(31);
        Assert.Equal("reverted", fixture.Engine.Confirm().Status);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity())); Assert.False(fixture.Backend.Committed);
        var other = new Fixture(); other.Begin(); Assert.Equal("owner_exited", other.Engine.Tick(false).ReasonCode);
    }
    [Fact] public void ComparisonsAndRepeatedReviewCommandsDoNotExtendTheReviewDeadline()
    {
        var fixture = new Fixture();
        fixture.Begin(windowOwned: true);
        fixture.Clock.Advance(2);
        fixture.Engine.SetReviewMode(true);
        var deadline = fixture.Engine.Session!.ExpiresAt;

        fixture.Clock.Advance(15);
        Assert.True(fixture.Engine.Update(fixture.Preset, showOriginal: true, renew: true).ShowingOriginal);
        fixture.Engine.Renew();
        fixture.Engine.SetReviewMode(true);
        fixture.Clock.Advance(14);
        Assert.False(fixture.Engine.Update(fixture.Preset, showOriginal: false, renew: true).ShowingOriginal);
        Assert.Equal(deadline, fixture.Engine.Session.ExpiresAt);
        fixture.Clock.Advance(1);
        Assert.Equal("reverted", fixture.Engine.Tick().Status);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
    }

    [Fact] public void EnteringReviewAppliesPendingEditsBeforeStartingTheReviewDeadline()
    {
        var fixture = new Fixture();
        var preview = fixture.Begin(windowOwned: true, seconds: 60);
        var edited = fixture.Preset with { Calibration = new() { Gamma = 2.4, RedGain = .92 } };
        fixture.Clock.Advance(10);
        fixture.Backend.BeforeWrite = () =>
        {
            Assert.False(fixture.Engine.Session!.IsReviewing);
            Assert.Equal(preview.ExpiresAt, fixture.Engine.Session.ExpiresAt);
            fixture.Clock.Advance(1);
        };

        fixture.Engine.SetReviewMode(true, edited);

        var reviewing = fixture.Engine.Session!;
        Assert.True(reviewing.IsReviewing);
        Assert.False(reviewing.ShowingOriginal);
        Assert.Equal(edited, reviewing.Preset);
        Assert.True(fixture.Backend.State.Ramp.Matches(reviewing.Candidate));
        Assert.Equal(preview.StartedAt.AddSeconds(41), reviewing.ExpiresAt);
    }

    [Fact] public void ReviewDoesNotStartWhenPendingEditsCrossTheExistingDeadline()
    {
        var fixture = new Fixture();
        fixture.Begin(windowOwned: true);
        fixture.Clock.Advance(29);
        fixture.Backend.BeforeWrite = () => fixture.Clock.Advance(2);

        fixture.Engine.SetReviewMode(true, fixture.Preset with { Calibration = new() { Gamma = 2.4 } });

        Assert.Equal("reverted", fixture.Engine.Session!.Status);
        Assert.Equal("preview_expired", fixture.Engine.Session.ReasonCode);
        Assert.False(fixture.Engine.Session.IsReviewing);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
    }

    [Fact] public void LeavingOriginalComparisonResumesEditingWithTheLatestAdjustedCurve()
    {
        var fixture = new Fixture();
        var preview = fixture.Begin(windowOwned: true, seconds: 60);
        fixture.Engine.SetReviewMode(true);
        fixture.Engine.Update(fixture.Preset, showOriginal: true);
        var edited = fixture.Preset with { Calibration = new() { GreenGain = .91 } };
        fixture.Clock.Advance(5);

        fixture.Engine.SetReviewMode(false, edited);

        var editing = fixture.Engine.Session!;
        Assert.False(editing.IsReviewing);
        Assert.False(editing.ShowingOriginal);
        Assert.Equal(edited, editing.Preset);
        Assert.True(fixture.Backend.State.Ramp.Matches(editing.Candidate));
        Assert.Equal(preview.StartedAt.AddSeconds(65), editing.ExpiresAt);
    }

    [Fact] public void CliPreviewRetainsDeadlineAfterEditsAndWindowCommands()
    {
        var fixture = new Fixture();
        var preview = fixture.Begin();
        fixture.Clock.Advance(25);
        fixture.Engine.Update(fixture.Preset with { Calibration = new() { BlueGain = .8 } }, renew: true);
        fixture.Engine.Renew();
        fixture.Engine.SetReviewMode(true, fixture.Preset);
        Assert.Equal(preview.ExpiresAt, fixture.Engine.Session!.ExpiresAt);
        Assert.False(fixture.Engine.Session.IsReviewing);
        Assert.Equal(.8, fixture.Engine.Session.Preset.Calibration.BlueGain);
        fixture.Clock.Advance(5);
        Assert.Equal("preview_expired", fixture.Engine.Confirm().ReasonCode);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
        Assert.False(fixture.Backend.Committed);
    }

    [Fact] public void WindowHeartbeatAndReviewStayWithinSessionLimit()
    {
        var fixture = new Fixture();
        var preview = fixture.Begin(windowOwned: true, seconds: 60);
        for (int elapsed = 50; elapsed < 1800; elapsed += 50)
        {
            fixture.Clock.Advance(50);
            fixture.Engine.Renew();
        }
        fixture.Clock.Advance(45);
        fixture.Engine.SetReviewMode(true);
        Assert.Equal(preview.StartedAt.AddMinutes(30), fixture.Engine.Session!.ExpiresAt);
        fixture.Clock.Advance(5);
        Assert.Equal("preview_expired", fixture.Engine.Tick().ReasonCode);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
    }

    [Fact] public void LateWindowHeartbeatRevertsThePreview()
    {
        var fixture = new Fixture();
        fixture.Begin(windowOwned: true);
        fixture.Clock.Advance(30);
        fixture.Engine.Renew();
        Assert.Equal("preview_expired", fixture.Engine.Session!.ReasonCode);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
    }

    [Fact] public void InitialPreviewExpiresDuringItsFinalDisplayRead()
    {
        var fixture = new Fixture();
        int reads = 0;
        fixture.Backend.BeforeRead = () =>
        {
            if (++reads == 4) fixture.Clock.Advance(30);
        };

        var result = fixture.Begin();

        Assert.Equal("reverted", result.Status);
        Assert.Equal("preview_expired", result.ReasonCode);
        Assert.True(fixture.Backend.State.Ramp.Matches(result.Original.Ramp));
    }

    [Fact] public void WindowExitDuringInitialReadbackRollsBackBeforeAdvertisingThePreview()
    {
        bool ownerAlive = true;
        var fixture = new Fixture(() => ownerAlive);
        fixture.Backend.BeforeRead = () =>
        {
            if (fixture.Backend.Writes.Count == 1) ownerAlive = false;
        };
        bool published = false;
        fixture.Journal.BeforeSave = state => published |= state.Status == "previewing";

        var result = fixture.Begin(windowOwned: true);

        Assert.False(published);
        Assert.Equal("reverted", result.Status);
        Assert.Equal("owner_exited", result.ReasonCode);
        Assert.True(result.CurveReadbackVerified);
        Assert.Equal(2, fixture.Backend.Writes.Count);
        Assert.True(fixture.Backend.State.Ramp.Matches(result.Original.Ramp));
    }

    [Theory]
    [InlineData("journal", false)]
    [InlineData("journal", true)]
    [InlineData("native", false)]
    [InlineData("native", true)]
    public void ExpiredInitialPreviewDoesNotWriteOrClaimTheCandidate(string stage, bool externalWrite)
    {
        var fixture = new Fixture();
        var original = fixture.Backend.State.Ramp;
        var candidate = GammaRampMath.Compose(original, fixture.Preset.Calibration);
        void Expire()
        {
            if (externalWrite) fixture.Backend.State = fixture.Backend.State with { Ramp = candidate.Copy() };
            fixture.Clock.Advance(30);
        }
        if (stage == "journal") fixture.Journal.BeforeSave = session => { if (session.Status == "applying") Expire(); };
        else fixture.Backend.BeforeWrite = Expire;

        var result = fixture.Begin();

        Assert.Empty(fixture.Backend.Writes);
        Assert.True(fixture.Backend.State.Ramp.Matches(externalWrite ? candidate : original));
        Assert.Equal("reverted", result.Status);
        Assert.Equal("preview_expired", result.ReasonCode);
        Assert.False(result.CurveReadbackVerified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiredUpdateRollsBackOnlyThePreviouslyAppliedCurve(bool externalWrite)
    {
        var fixture = new Fixture();
        var preview = fixture.Begin();
        var edit = fixture.Preset with { Calibration = new() { GreenGain = .8 } };
        var requested = GammaRampMath.Compose(preview.Original.Ramp, edit.Calibration);
        fixture.Backend.BeforeWrite = () =>
        {
            fixture.Backend.BeforeWrite = null;
            if (externalWrite) fixture.Backend.State = fixture.Backend.State with { Ramp = requested.Copy() };
            fixture.Clock.Advance(30);
        };

        var result = fixture.Engine.Update(edit);

        Assert.DoesNotContain(fixture.Backend.Writes, written => written.Matches(requested));
        Assert.Equal(preview.ExpiresAt, result.ExpiresAt);
        Assert.Equal(externalWrite ? "superseded" : "reverted", result.Status);
        Assert.Equal(externalWrite ? "external_curve_change" : "preview_expired", result.ReasonCode);
        Assert.True(fixture.Backend.State.Ramp.Matches(externalWrite ? requested : preview.Original.Ramp));
    }

    [Fact] public void RejectedUpdateRetainsAnExternallyWrittenCurveThatMatchesTheEdit()
    {
        var fixture = new Fixture();
        var preview = fixture.Begin();
        var edit = fixture.Preset with { Calibration = new() { BlueGain = .7 } };
        var requested = GammaRampMath.Compose(preview.Original.Ramp, edit.Calibration);
        fixture.Backend.BeforeWrite = () =>
        {
            fixture.Backend.BeforeWrite = null;
            fixture.Backend.State = fixture.Backend.State with { Ramp = requested.Copy() };
            throw new ProfilePreviewException("display_state_changed", "Simulated final validation failure.");
        };

        Assert.Equal("display_state_changed", Assert.Throws<ProfilePreviewException>(() => fixture.Engine.Update(edit)).Code);

        Assert.Single(fixture.Backend.Writes);
        Assert.Equal("superseded", fixture.Engine.Session!.Status);
        Assert.True(fixture.Backend.State.Ramp.Matches(requested));
    }

    [Fact] public void ExpiryDuringDisplayReadDoesNotOverwriteAnOutsideProfileChange()
    {
        var fixture = new Fixture();
        var preview = fixture.Begin();
        fixture.Backend.BeforeRead = () =>
        {
            fixture.Clock.Advance(30);
            fixture.Backend.State = fixture.Backend.State with { ProfileName = "external.icc" };
        };
        fixture.Backend.BeforeWrite = () => throw new InvalidOperationException("Do not overwrite an outside change.");

        var result = fixture.Engine.Tick();

        Assert.Equal("superseded", result.Status);
        Assert.Equal("external_profile_change", result.ReasonCode);
        Assert.Equal("external.icc", fixture.Backend.State.ProfileName);
        Assert.True(fixture.Backend.State.Ramp.Matches(preview.Candidate));
    }

    [Fact] public void UpdateExpiresWhileThePreviewJournalIsBeingSaved()
    {
        var fixture = new Fixture();
        var preview = fixture.Begin();
        fixture.Journal.BeforeSave = session =>
        {
            if (session.Status == "previewing") fixture.Clock.Advance(30);
        };

        var result = fixture.Engine.Update(fixture.Preset with { Calibration = new() { GreenGain = .8 } });

        Assert.Equal("reverted", result.Status);
        Assert.Equal("preview_expired", result.ReasonCode);
        Assert.Equal(preview.ExpiresAt, result.ExpiresAt);
        Assert.True(fixture.Backend.State.Ramp.Matches(preview.Original.Ramp));
    }

    [Fact] public void ReviewExpiresWhileTheNewDeadlineIsBeingSaved()
    {
        var fixture = new Fixture();
        var preview = fixture.Begin(windowOwned: true);
        fixture.Journal.BeforeSave = session =>
        {
            if (session is { Status: "previewing", IsReviewing: true }) fixture.Clock.Advance(30);
        };

        fixture.Engine.SetReviewMode(true);

        Assert.Equal("reverted", fixture.Engine.Session!.Status);
        Assert.Equal("preview_expired", fixture.Engine.Session.ReasonCode);
        Assert.True(fixture.Backend.State.Ramp.Matches(preview.Original.Ramp));
    }

    [Fact] public void ConfirmCannotCommitIfTheDeadlinePassesDuringDisplayRead()
    {
        var fixture = new Fixture();
        fixture.Begin();
        int reads = 0;
        fixture.Backend.BeforeRead = () =>
        {
            if (++reads == 2) fixture.Clock.Advance(30);
        };
        var result = fixture.Engine.Confirm();
        Assert.Equal("reverted", result.Status);
        Assert.Equal("preview_expired", result.ReasonCode);
        Assert.False(fixture.Backend.Committed);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
    }

    [Fact] public void ConfirmCannotRewriteTheCurveIfFinalDriverPreparationCrossesTheDeadline()
    {
        var fixture = new Fixture();
        var preview = fixture.Begin();
        fixture.Backend.BeforeWrite = () => fixture.Clock.Advance(30);

        var result = fixture.Engine.Confirm();

        Assert.Equal("reverted", result.Status);
        Assert.Equal("preview_expired", result.ReasonCode);
        Assert.False(fixture.Backend.Committed);
        Assert.Collection(fixture.Backend.Writes,
            written => Assert.True(written.Matches(preview.Candidate)),
            written => Assert.True(written.Matches(preview.Original.Ramp)));
    }

    [Fact] public void WindowExitWhileRecordingConfirmationPreventsAnIccCommit()
    {
        bool ownerAlive = true;
        var fixture = new Fixture(() => ownerAlive);
        var preview = fixture.Begin(windowOwned: true);
        fixture.Journal.BeforeSave = state => { if (state.Status == "committing") ownerAlive = false; };

        var result = fixture.Engine.Confirm(new BindingSaveIntent(null));

        Assert.Equal("reverted", result.Status);
        Assert.Equal("owner_exited", result.ReasonCode);
        Assert.False(result.ProfileCommitStarted);
        Assert.False(result.NeedsBindingCompletion);
        Assert.False(fixture.Backend.Committed);
        Assert.False(fixture.Backend.ProfileRestoreCalled);
        Assert.True(fixture.Backend.State.Ramp.Matches(preview.Original.Ramp));
    }
    [Fact] public void CommitFailureRestoresTheCurveAndOriginalAssociation()
    {
        var fixture = new Fixture(); fixture.Begin(); fixture.Backend.FailCommit = true;
        Assert.Throws<IOException>(() => fixture.Engine.Confirm());
        Assert.Equal("reverted", fixture.Engine.Session!.Status);
        Assert.True(fixture.Backend.ProfileRestoreCalled);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
    }
    [Fact] public void UpdateFailureRestoresEvenWhenThePreviousOwnCurveWasActive()
    {
        var fixture = new Fixture(); fixture.Begin();
        fixture.Engine.Update(fixture.Preset with { Calibration = new() { GreenGain = .8 } });
        fixture.Backend.FailNextWrite = true;
        Assert.Throws<IOException>(() => fixture.Engine.Update(fixture.Preset with { Calibration = new() { RedGain = .7 } }));
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedUpdateRecoversFromEitherSideOfTheDriverWrite(bool writeReachedDriver)
    {
        var fixture = new Fixture();
        fixture.Begin();
        var newPreset = fixture.Preset with { Calibration = new() { GreenGain = .8 } };
        fixture.Engine.Update(newPreset);
        var interrupted = fixture.Journal.LastApplying!;
        if (!writeReachedDriver)
            fixture.Backend.State = fixture.Backend.State with { Ramp = interrupted.BeforeWrite!.Copy() };

        var recovered = new GammaCalibrationTransaction(fixture.Backend, fixture.Journal).Recover(interrupted);

        Assert.Equal("reverted", recovered.Status);
        Assert.True(fixture.Backend.State.Ramp.Matches(interrupted.Original.Ramp));
    }
    [Fact] public void UpdateFailureDoesNotOverwriteAnOutsideChange()
    {
        var fixture = new Fixture();
        fixture.Begin();
        var external = GammaRampMath.Compose(GammaRamp.Identity(), new() { BlueGain = .75 });
        fixture.Backend.BeforeWrite = () =>
        {
            fixture.Backend.State = fixture.Backend.State with { Ramp = external.Copy() };
            throw new IOException("Write interrupted by an external change.");
        };

        Assert.Throws<IOException>(() => fixture.Engine.Update(fixture.Preset with { Calibration = new() { RedGain = .7 } }));

        Assert.Equal("superseded", fixture.Engine.Session!.Status);
        Assert.True(fixture.Backend.State.Ramp.Matches(external));
    }
    [Fact] public void TopologyChangeEndsPreviewAndRestoresTheSameIndependentPath()
    {
        var fixture = new Fixture();
        fixture.Begin();
        fixture.Backend.State = fixture.Backend.State with { TopologyFingerprint = new string('B', 64) };

        Assert.Equal("reverted", fixture.Engine.Tick().Status);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActiveTopologyChangePreventsConfirmingTheCurve(bool hasIdentity)
    {
        var fixture = new Fixture();
        string originalTopology = new('A', 64);
        fixture.Backend.State = fixture.Backend.State with
        {
            ActiveTopologySha256 = originalTopology,
            PersistenceKey = hasIdentity ? fixture.Backend.State.PersistenceKey : null
        };
        var started = fixture.Begin();
        fixture.Backend.State = fixture.Backend.State with { ActiveTopologySha256 = new string('B', 64) };

        var result = fixture.Engine.Confirm();

        Assert.False(fixture.Backend.Committed);
        if (!hasIdentity)
        {
            Assert.Equal("recovery-pending", result.Status);
            Assert.True(fixture.Backend.State.Ramp.Matches(started.Candidate));
            fixture.Backend.State = fixture.Backend.State with { ActiveTopologySha256 = originalTopology };
            result = new GammaCalibrationTransaction(fixture.Backend, fixture.Journal).Recover(result);
        }
        Assert.Equal("reverted", result.Status);
        Assert.True(fixture.Backend.State.Ramp.Matches(started.Original.Ramp));
    }
    [Theory]
    [InlineData("disconnected")]
    [InlineData("remote_session_blocked")]
    [InlineData("source-reused")]
    [InlineData("different-monitor")]
    [InlineData("mirrored")]
    [InlineData("hdr")]
    public void InterruptedPreviewDefersUntilTheOriginalSdrPathReturns(string change)
    {
        var fixture = new Fixture();
        fixture.Begin();
        var active = fixture.Backend.State with { TopologyFingerprint = new string('B', 64) };
        fixture.Backend.Disconnected = change == "disconnected";
        if (change == "remote_session_blocked")
            fixture.Backend.BeforeRead = () => throw new ProfilePreviewException(change, "Remote calibration is unavailable.");
        fixture.Backend.State = change switch
        {
            "source-reused" => active with { SourceId = 9 },
            "different-monitor" => active with { PersistenceKey = "path:" + new string('C', 64) },
            "mirrored" => active with { SharedSource = true },
            "hdr" => active with { ExplicitSdr = false },
            _ => active,
        };

        var pending = fixture.Engine.Tick();
        Assert.Equal("recovery-pending", pending.Status);
        if (change == "remote_session_blocked") Assert.Equal(change, pending.ReasonCode);
        Assert.True(fixture.Backend.State.Ramp.Matches(active.Ramp));

        fixture.Backend.Disconnected = false;
        fixture.Backend.BeforeRead = null;
        fixture.Backend.State = active;
        var recovered = new GammaCalibrationTransaction(fixture.Backend, fixture.Journal).Recover(pending);
        Assert.Equal("reverted", recovered.Status);
        Assert.True(fixture.Backend.State.Ramp.Matches(pending.Original.Ramp));
    }
    [Fact] public void ConfirmRequiresTheInstalledProfileAndFailedCommitRestoresAssociation()
    {
        var fixture = new Fixture();
        fixture.Begin();
        fixture.Backend.MisreportCommit = true;

        Assert.Equal("profile_selection_changed", Assert.Throws<ProfilePreviewException>(() => fixture.Engine.Confirm()).Code);

        Assert.Equal("reverted", fixture.Engine.Session!.Status);
        Assert.True(fixture.Backend.ProfileRestoreCalled);
        Assert.Null(fixture.Backend.State.ProfileName);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
    }

    [Theory]
    [InlineData("effective")]
    [InlineData("selected")]
    public void SavingCannotConfirmTheSameProfileUnderADifferentScope(string changedScope)
    {
        var fixture = new Fixture();
        fixture.Begin();
        fixture.Backend.AfterCommit = () => fixture.Backend.State = changedScope == "effective"
            ? fixture.Backend.State with { ProfileScope = "system-wide" }
            : fixture.Backend.State with { SelectedProfileScope = "system-wide" };

        var error = Assert.Throws<ProfilePreviewException>(() => fixture.Engine.Confirm());

        Assert.Equal("profile_selection_changed", error.Code);
        Assert.Equal("reverted", fixture.Engine.Session!.Status);
        Assert.Equal(error.Code, fixture.Engine.Session.ReasonCode);
        Assert.False(fixture.Engine.Session.NeedsBindingCompletion);
        Assert.True(fixture.Backend.ProfileRestoreCalled);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
    }

    [Theory]
    [InlineData("matched")]
    [InlineData("device-changed")]
    [InlineData("hdr")]
    [InlineData("legacy")]
    [InlineData("path-changes-again")]
    public void InterruptedSaveChecksTheReassignedPathBeforeRestoring(string condition)
    {
        var fixture = new Fixture();
        string hash = new('F', 64);
        fixture.Backend.State = fixture.Backend.State with { ProfileDeviceSha256 = condition == "legacy" ? null : hash };
        var interrupted = BeginInterruptedCommit(fixture);
        var remapped = fixture.Backend.State with
        {
            DisplayId = "fedcba9876543210", DeviceName = @"\\.\DISPLAY2", TargetKey = "00000000:00000002:2", SourceId = 4,
            ProfileDeviceSha256 = condition == "device-changed" ? new string('E', 64) : hash,
            ExplicitSdr = condition != "hdr"
        };
        fixture.Backend.State = remapped;
        fixture.Backend.Remapped = true;
        if (condition == "path-changes-again")
            fixture.Backend.AfterRestoreProfile = () => fixture.Backend.State = fixture.Backend.State with { SourceId = 5 };

        var result = new GammaCalibrationTransaction(fixture.Backend, fixture.Journal).Recover(interrupted);

        Assert.Equal(interrupted.Original, result.Original);
        Assert.Equal(condition is "matched" or "path-changes-again", fixture.Backend.ProfileRestoreCalled);
        Assert.Equal(condition == "matched" ? "reverted" : "recovery-pending", result.Status);
        Assert.True(fixture.Backend.State.Ramp.Matches(condition == "matched" ? interrupted.Original.Ramp : remapped.Ramp));
    }
    [Fact] public void ExternalCurveAndTopologyChangesAreNotOverwritten()
    {
        var fixture = new Fixture(); fixture.Begin();
        fixture.Backend.State = fixture.Backend.State with { Ramp = GammaRampMath.Compose(GammaRamp.Identity(), new() { Gamma = 1.3 }) };
        Assert.Equal("superseded", fixture.Engine.Tick().Status);
        var other = new Fixture(); other.Begin();
        other.Backend.State = other.Backend.State with { TargetKey = "different" };
        Assert.Equal("recovery-pending", other.Engine.Tick().Status);
    }
    [Fact] public void IccWriterPreservesCharacterizationAndSerializesAllChannelsBigEndian()
    {
        byte[] source = Profile(); var ramp = GammaRampMath.Compose(GammaRamp.Identity(), new() { BlueGain = .75 });
        byte[] output = IccCalibrationWriter.Build(source, ramp, Guid.NewGuid());
        var report = IccProfileInspector.Inspect("result.icc", output);
        Assert.Equal("read", report.ReadStatus); Assert.True(report.VcgtPresent); Assert.True(report.TrcPresent);
        Assert.Equal(Tag(source, "rTRC"), Tag(output, "rTRC"));
        byte[] vcgt = Tag(output, "vcgt");
        Assert.Equal("vcgt", Encoding.ASCII.GetString(vcgt, 0, 4));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(vcgt.AsSpan(12)));
        Assert.Equal(256, BinaryPrimitives.ReadUInt16BigEndian(vcgt.AsSpan(14)));
        Assert.Equal(ramp.Blue[255], BinaryPrimitives.ReadUInt16BigEndian(vcgt.AsSpan(18 + (2 * 256 + 255) * 2)));
    }
    [Fact] public void ExternalProfileSelectionEndsPreviewWithoutRestoringOurOldCurve()
    {
        var fixture = new Fixture(); fixture.Begin();
        fixture.Backend.State = fixture.Backend.State with { ProfileName = "another.icc", ProfileScope = "current-user" };
        var current = fixture.Backend.State.Ramp.Copy();
        Assert.Equal("superseded", fixture.Engine.Tick().Status);
        Assert.True(current.Matches(fixture.Backend.State.Ramp));
        Assert.False(fixture.Backend.ProfileRestoreCalled);
    }
    [Fact] public void IccWriterRejectsMalformedAndMhc2Bases()
    {
        Assert.Throws<InvalidDataException>(() => IccCalibrationWriter.Build(new byte[10], GammaRamp.Identity(), Guid.NewGuid()));
        byte[] profile = Profile(); Encoding.ASCII.GetBytes("MHC2").CopyTo(profile, 132);
        Assert.Throws<InvalidDataException>(() => IccCalibrationWriter.Build(profile, GammaRamp.Identity(), Guid.NewGuid()));
    }
    [Fact] public void IccWriterBoundsGrowthWhenMultipleTagsShareTheSameLargePayload()
    {
        byte[] source = Profile();
        int payloadBytes = IccProfileInspector.MaxBytes / 2;
        Array.Resize(ref source, 168 + payloadBytes);
        BinaryPrimitives.WriteUInt32BigEndian(source, (uint)source.Length);
        for (int i = 0; i < 3; i++)
        {
            int entry = 132 + i * 12;
            BinaryPrimitives.WriteUInt32BigEndian(source.AsSpan(entry + 4), 168);
            BinaryPrimitives.WriteUInt32BigEndian(source.AsSpan(entry + 8), (uint)payloadBytes);
        }
        Assert.Equal("read", IccProfileInspector.Inspect("shared.icc", source).ReadStatus);
        Assert.Throws<InvalidDataException>(() => IccCalibrationWriter.Build(source, GammaRamp.Identity(), Guid.NewGuid()));
    }
    [Fact] public void GammaJournalRoundTripPreservesRecoveryAndRejectsAlteredIntent()
    {
        string root = Path.Combine(Path.GetTempPath(), "dccwpp-gamma-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = new Fixture();
            fixture.Backend.State = fixture.Backend.State with { ActiveTopologySha256 = new string('B', 64) };
            fixture.Begin(); var session = fixture.Journal.LastApplying!; var store = new GammaCalibrationStore(root);
            store.Save(session); var loaded = store.Load(session.Id);
            Assert.True(loaded.Candidate.Matches(session.Candidate)); Assert.True(loaded.NeedsRecovery);
            Assert.True(loaded.BeforeWrite!.Matches(session.Original.Ramp));
            Assert.Equal(fixture.Backend.State.PersistenceKey, loaded.Original.PersistenceKey);
            Assert.Equal(fixture.Backend.State.ActiveTopologySha256, loaded.Original.ActiveTopologySha256);
            Assert.Throws<InvalidDataException>(() => store.Save(session with { Preset = session.Preset with { Calibration = new() { Gamma = 1.1 } } }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public void RestoringAnAcceptedCurveDoesNotApplyThePresetASecondTime()
    {
        var fixture = new Fixture();
        var accepted = GammaRampMath.Compose(GammaRamp.Identity(), fixture.Preset.Calibration);
        fixture.Backend.State = fixture.Backend.State with { Ramp = accepted.Copy() };
        var session = fixture.Engine.Begin(Guid.NewGuid(), fixture.Preset, fixture.Backend.State.DisplayId, 30, accepted);
        Assert.True(session.IsBindingRestore);
        Assert.True(fixture.Backend.State.Ramp.Matches(accepted));
    }
    private static byte[] Profile()
    {
        var bytes = new byte[192];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length); bytes[8] = 4; bytes[9] = 0x30;
        "mntr"u8.CopyTo(bytes.AsSpan(12)); "RGB "u8.CopyTo(bytes.AsSpan(16)); "XYZ "u8.CopyTo(bytes.AsSpan(20)); "acsp"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(128), 3);
        string[] tags = ["rTRC", "gTRC", "bTRC"];
        for (int i = 0; i < 3; i++)
        {
            int entry = 132 + i * 12; Encoding.ASCII.GetBytes(tags[i]).CopyTo(bytes, entry);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 4), (uint)(168 + i * 8));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 8), 8); "curv"u8.CopyTo(bytes.AsSpan(168 + i * 8));
        }
        return bytes;
    }
    private static byte[] Tag(byte[] profile, string name)
    {
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128));
        for (int i = 0; i < count; i++)
        {
            int entry = 132 + 12 * i;
            if (Encoding.ASCII.GetString(profile, entry, 4) != name) continue;
            return profile.AsSpan((int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(entry + 4)),
                (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(entry + 8))).ToArray();
        }
        throw new InvalidDataException("Tag missing.");
    }
    private sealed class Fixture
    {
        public FakeBackend Backend { get; } = new(); public FakeClock Clock { get; } = new();
        public Journal Journal { get; } = new();
        public CalibrationPreset Preset { get; } = CalibrationPreset.Create("Test", new() { RedGain = .9 });
        public GammaCalibrationTransaction Engine { get; }
        public Fixture(Func<bool>? isOwnerAlive = null) => Engine = new(Backend, Journal, Clock, isOwnerAlive);
        public GammaCalibrationSession Begin(bool windowOwned = false, int seconds = 30) =>
            Engine.Begin(Guid.NewGuid(), Preset, Backend.State.DisplayId, seconds, windowOwned: windowOwned);
    }
    private sealed class Journal : IGammaCalibrationJournal
    {
        public Action<GammaCalibrationSession>? BeforeSave { get; set; }
        public GammaCalibrationSession? LastApplying { get; private set; }
        public GammaCalibrationSession? LastCommitting { get; private set; }
        public void Save(GammaCalibrationSession session)
        {
            BeforeSave?.Invoke(session);
            if (session.Status == "applying") LastApplying = session;
            if (session.Status == "committing") LastCommitting = session;
        }
    }
    private sealed class FakeClock : TimeProvider
    {
        private long _ticks; public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        public void Advance(int seconds) => _ticks += seconds * TimeSpan.TicksPerSecond;
    }
    private sealed class FakeBackend : IGammaCalibrationBackend
    {
        public GammaDeviceState State { get; set; } = new("0123456789abcdef", @"\\.\DISPLAY1", "00000000:00000001:1",
            new string('A', 64), true, false, GammaRamp.Identity(), SourceId: 1, PersistenceKey: "path:" + new string('D', 64));
        public bool Committed, FailCommit, FailNextWrite, ProfileRestoreCalled, Disconnected, MisreportCommit;
        public bool Remapped;
        public List<GammaRamp> Writes { get; } = [];
        public Action? BeforeRead, BeforeWrite, BeforeRestoreProfile, BeforeChangingProfile, AfterRestoreProfile, BeforeCommit, AfterCommit;
        public GammaRamp? RestoredProfileRamp;
        public GammaDeviceState Read(string displayId)
        {
            BeforeRead?.Invoke();
            if (Disconnected || (Remapped && displayId != State.DisplayId))
                throw new ProfilePreviewException("display_disconnected", "Display disconnected.");
            return State with { Ramp = State.Ramp.Copy() };
        }
        public GammaDeviceState ReadRecoveryTarget(GammaDeviceState original) => Read(Remapped ? State.DisplayId : original.DisplayId);
        public bool TryWrite(GammaDeviceState expected, GammaRamp ramp, Func<bool>? canWrite = null)
        {
            BeforeWrite?.Invoke();
            if (canWrite?.Invoke() == false) return false;
            if (FailNextWrite) { FailNextWrite = false; throw new IOException("write failure"); }
            Writes.Add(ramp.Copy());
            State = State with { Ramp = ramp.Copy() };
            return true;
        }
        public GammaProfileSelection Commit(GammaCalibrationSession session)
        {
            BeforeCommit?.Invoke();
            if (FailCommit) throw new IOException("commit failure");
            Committed = true;
            const string installed = "DCCWPlus-test.icc";
            State = State with { ProfileName = MisreportCommit ? null : installed, ProfileScope = "current-user", SelectedProfileScope = "current-user" };
            AfterCommit?.Invoke();
            return new(installed, "current-user", "current-user");
        }
        public bool RestoreProfile(GammaCalibrationSession session, GammaDeviceState expected, Action? beforeSelectionRestore = null)
        {
            BeforeRestoreProfile?.Invoke();
            ProfileRestoreCalled = true;
            bool changed = !GammaCalibrationTransaction.SameProfile(session.Original, State);
            bool matches = GammaCalibrationTransaction.SameTarget(expected, State) && GammaCalibrationTransaction.SameProfile(expected, State)
                && State.Ramp.Matches(expected.Ramp, GammaCalibrationTransaction.ReadbackTolerance);
            if (changed && matches) beforeSelectionRestore?.Invoke();
            BeforeChangingProfile?.Invoke();
            State = State with
            {
                ProfileName = session.Original.ProfileName, ProfileScope = session.Original.ProfileScope,
                SelectedProfileScope = session.Original.SelectedProfileScope,
                Ramp = changed && RestoredProfileRamp is not null ? RestoredProfileRamp.Copy() : State.Ramp
            };
            AfterRestoreProfile?.Invoke();
            return changed && matches;
        }
    }
}
