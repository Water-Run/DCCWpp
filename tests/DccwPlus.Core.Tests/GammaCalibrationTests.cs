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

    [Fact] public void CliPreviewRetainsDeadlineAfterEditsAndWindowCommands()
    {
        var fixture = new Fixture();
        var preview = fixture.Begin();
        fixture.Clock.Advance(25);
        fixture.Engine.Update(fixture.Preset with { Calibration = new() { BlueGain = .8 } }, renew: true);
        fixture.Engine.Renew();
        fixture.Engine.SetReviewMode(true);
        Assert.Equal(preview.ExpiresAt, fixture.Engine.Session!.ExpiresAt);
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
    [InlineData("disconnected")]
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
        Assert.True(fixture.Backend.State.Ramp.Matches(active.Ramp));

        fixture.Backend.Disconnected = false;
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

        Assert.Throws<IOException>(() => fixture.Engine.Confirm());

        Assert.Equal("reverted", fixture.Engine.Session!.Status);
        Assert.True(fixture.Backend.ProfileRestoreCalled);
        Assert.Null(fixture.Backend.State.ProfileName);
        Assert.True(fixture.Backend.State.Ramp.Matches(GammaRamp.Identity()));
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
    [Fact] public void GammaJournalRoundTripPreservesRecoveryAndRejectsAlteredIntent()
    {
        string root = Path.Combine(Path.GetTempPath(), "dccwpp-gamma-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = new Fixture(); fixture.Begin(); var session = fixture.Journal.LastApplying!; var store = new GammaCalibrationStore(root);
            store.Save(session); var loaded = store.Load(session.Id);
            Assert.True(loaded.Candidate.Matches(session.Candidate)); Assert.True(loaded.NeedsRecovery);
            Assert.True(loaded.BeforeWrite!.Matches(session.Original.Ramp));
            Assert.Equal(fixture.Backend.State.PersistenceKey, loaded.Original.PersistenceKey);
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
        public Fixture() => Engine = new(Backend, Journal, Clock);
        public GammaCalibrationSession Begin(bool windowOwned = false, int seconds = 30) =>
            Engine.Begin(Guid.NewGuid(), Preset, Backend.State.DisplayId, seconds, windowOwned: windowOwned);
    }
    private sealed class Journal : IGammaCalibrationJournal
    {
        public GammaCalibrationSession? LastApplying { get; private set; }
        public void Save(GammaCalibrationSession session)
        {
            if (session.Status == "applying") LastApplying = session;
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
        public Action? BeforeRead, BeforeWrite, BeforeRestoreProfile;
        public GammaDeviceState Read(string displayId)
        {
            BeforeRead?.Invoke();
            if (Disconnected) throw new ProfilePreviewException("display_disconnected", "Display disconnected.");
            return State with { Ramp = State.Ramp.Copy() };
        }
        public void Write(GammaDeviceState expected, GammaRamp ramp)
        {
            BeforeWrite?.Invoke();
            if (FailNextWrite) { FailNextWrite = false; throw new IOException("write failure"); }
            State = State with { Ramp = ramp.Copy() };
        }
        public string Commit(GammaCalibrationSession session)
        {
            if (FailCommit) throw new IOException("commit failure");
            Committed = true;
            const string installed = "DCCWPlus-test.icc";
            State = State with { ProfileName = MisreportCommit ? null : installed, ProfileScope = "current-user" };
            return installed;
        }
        public void RestoreProfile(GammaCalibrationSession session)
        {
            BeforeRestoreProfile?.Invoke();
            ProfileRestoreCalled = true;
            State = State with { ProfileName = session.Original.ProfileName, ProfileScope = session.Original.ProfileScope };
        }
    }
}
