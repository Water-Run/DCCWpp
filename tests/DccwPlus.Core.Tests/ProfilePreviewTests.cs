using DccwPlus.Core.Calibration;
using DccwPlus.Core.Storage;
using Xunit;

namespace DccwPlus.Core.Tests;

public sealed class ProfilePreviewTests
{
    [Fact] public void RecoveryJournalPrecedesNativeWriteAndConfirmIsIdempotent()
    {
        var setup = new Fixture();
        setup.Backend.BeforeWrite = () => Assert.Equal("applying", setup.Journal.Last!.Status);
        var preview = setup.Start();
        Assert.Equal("previewing", preview.Status);
        Assert.True(preview.AssociationVerified);
        Assert.False(preview.ColorAccuracyVerified);
        Assert.Equal("confirmed", setup.Transaction.Confirm().Status);
        setup.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("confirmed", setup.Transaction.Tick(false).Status);
        Assert.Equal("confirmed", setup.Transaction.Confirm().Status);
        Assert.Single(setup.Backend.Writes);
    }

    [Fact] public void ExpirationAndLateConfirmationRestoreOriginal()
    {
        var setup = new Fixture(); setup.Start();
        setup.Clock.Advance(TimeSpan.FromSeconds(30));
        var state = setup.Transaction.Confirm();
        Assert.Equal("reverted", state.Status);
        Assert.Equal("preview_expired", state.ReasonCode);
        Assert.Equal("original.icc", setup.Backend.State.CurrentProfile);
        Assert.Equal(2, setup.Backend.Writes.Count);
        Assert.Equal("reverted", setup.Transaction.Confirm().Status);
        Assert.Equal(2, setup.Backend.Writes.Count);
    }

    [Fact] public void WallClockMovingBackCannotExtendPreview()
    {
        var setup = new Fixture(); setup.Start();
        setup.Clock.Timestamp += 31 * TimeSpan.TicksPerSecond;
        setup.Clock.Utc -= TimeSpan.FromHours(2);
        Assert.Equal("reverted", setup.Transaction.Tick().Status);
    }

    [Fact] public void WindowOwnerDeathReverts()
    {
        var setup = new Fixture(); setup.Start();
        Assert.Equal("owner_exited", setup.Transaction.Tick(ownerAlive: false).ReasonCode);
        Assert.Equal("original.icc", setup.Backend.State.CurrentProfile);
    }

    [Fact] public void ExternalApplicationChangeIsPreserved()
    {
        var setup = new Fixture(); setup.Start();
        setup.Backend.State = setup.Backend.State with { CurrentProfile = "external.icc" };
        Assert.Equal("superseded", setup.Transaction.Tick().Status);
        Assert.Equal("external.icc", setup.Backend.State.CurrentProfile);
        Assert.Single(setup.Backend.Writes);
    }

    [Fact] public void DisconnectedOrChangedTargetRetainsRecoveryJournalWithoutWritingAnotherDisplay()
    {
        var setup = new Fixture(); setup.Start();
        setup.Backend.State = setup.Backend.State with { TopologyFingerprint = new string('E', 64) };
        var result = setup.Transaction.Tick();
        Assert.Equal("recovery-pending", result.Status);
        Assert.True(result.NeedsRecovery);
        Assert.Single(setup.Backend.Writes);
        setup.Backend.State = setup.Backend.State with { TopologyFingerprint = new string('A', 64) };
        var recovered = new ProfilePreviewTransaction(setup.Backend, setup.Journal).Recover(result);
        Assert.Equal("reverted", recovered.Status);
        Assert.Equal("original.icc", setup.Backend.State.CurrentProfile);
    }

    [Fact] public void ChangedOriginalFilePreventsRestoringDifferentBytes()
    {
        var setup = new Fixture(); setup.Start();
        setup.Backend.State = setup.Backend.State with { Profiles =
            [new("original.icc", new string('E', 64), true), new("candidate.icc", new string('C', 64), true)] };
        Assert.Equal("original_profile_changed", setup.Transaction.Revert().ReasonCode);
        Assert.Single(setup.Backend.Writes);
    }

    [Fact] public void NativeFailureAfterPartialWriteStillRollsBack()
    {
        var setup = new Fixture(); setup.Backend.FailFirstAfterWrite = true;
        Assert.Throws<IOException>(() => setup.Start());
        Assert.Equal("original.icc", setup.Backend.State.CurrentProfile);
        Assert.Equal("reverted", setup.Transaction.Session!.Status);
    }

    [Fact] public void InitialJournalFailureDoesNotTouchDisplay()
    {
        var setup = new Fixture(); setup.Journal.FailStatus = "prepared";
        Assert.Throws<IOException>(() => setup.Start());
        Assert.Empty(setup.Backend.Writes);
    }

    [Fact] public void ConfirmationJournalFailureForcesRollback()
    {
        var setup = new Fixture(); setup.Start(); setup.Journal.FailStatus = "confirmed";
        Assert.Throws<IOException>(() => setup.Transaction.Confirm());
        Assert.Equal("original.icc", setup.Backend.State.CurrentProfile);
    }

    [Theory][InlineData(false, false, "current-user", "sdr_path_blocked")]
    [InlineData(true, true, "current-user", "shared_source_blocked")]
    [InlineData(true, false, "system-wide", "user_scope_required")]
    public void UnsupportedStatesNeverWrite(bool sdr, bool shared, string scope, string reason)
    {
        var setup = new Fixture();
        setup.Backend.State = setup.Backend.State with { ExplicitSdr = sdr, SharedSource = shared, Scope = scope };
        Assert.Equal(reason, Assert.Throws<ProfilePreviewException>(() => setup.Start()).Code);
        Assert.Empty(setup.Backend.Writes);
    }

    [Fact] public void JournalRoundTripsAndRejectsAnAlteredCandidateHash()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dccwpp-preview-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var setup = new Fixture(); var session = setup.Start(); var store = new ProfilePreviewStore(directory);
            store.Save(session); Assert.Equal(session.Id, store.Load(session.Id).Id);
            Assert.Single(store.Incomplete()); store.Request(session.Id, "revert"); store.Request(session.Id, "revert");
            Assert.True(store.Requested(session.Id, "revert"));
            Assert.Throws<InvalidDataException>(() => store.Save(session with { CandidateSha256 = new string('D', 64) }));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class Fixture
    {
        public FakeBackend Backend { get; } = new(); public FakeJournal Journal { get; } = new();
        public FakeClock Clock { get; } = new(); public ProfilePreviewTransaction Transaction { get; }
        public Fixture() => Transaction = new(Backend, Journal, Clock);
        public ProfilePreviewSession Start() => Transaction.Begin(Guid.NewGuid(), Backend.State.DisplayId, "candidate.icc", 30);
    }
    private sealed class FakeBackend : IProfileAssociationBackend
    {
        public ProfileAssociationState State { get; set; } = new("0123456789abcdef", "00000000:00000001:256", 0,
            new string('A', 64), "original.icc", "current-user", true, false,
            [new("original.icc", new string('B', 64), true), new("candidate.icc", new string('C', 64), true)]);
        public List<string> Writes { get; } = []; public Action? BeforeWrite { get; set; }
        public bool FailFirstAfterWrite { get; set; }
        public ProfileAssociationState Read(string id) => State;
        public void SetDefault(ProfileAssociationState expected, string profile, string hash)
        {
            BeforeWrite?.Invoke(); Writes.Add(profile); State = State with { CurrentProfile = profile };
            if (FailFirstAfterWrite && Writes.Count == 1) throw new IOException("Injected native failure.");
        }
    }
    private sealed class FakeJournal : IProfilePreviewJournal
    {
        public ProfilePreviewSession? Last { get; private set; } public string? FailStatus { get; set; }
        public void Save(ProfilePreviewSession session)
        { if (session.Status == FailStatus) throw new IOException("Injected journal failure."); Last = session; }
    }
    private sealed class FakeClock : TimeProvider
    {
        public long Timestamp; public DateTimeOffset Utc = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        public void Advance(TimeSpan duration) { Timestamp += duration.Ticks; Utc += duration; }
    }
}
