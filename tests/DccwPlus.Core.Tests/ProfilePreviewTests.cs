using DccwPlus.Core.Calibration;
using DccwPlus.Core.Storage;
using Xunit;

namespace DccwPlus.Core.Tests;

public sealed class ProfilePreviewTests
{
    private static readonly string PersistenceKey = "edid:" + new string('F', 64);

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

    [Theory]
    [InlineData("prepared")]
    [InlineData("applying")]
    [InlineData("backend-validation")]
    [InlineData("external-candidate")]
    public void StartupExpirationBeforeTheIccWriteDoesNotChangeTheDisplay(string stage)
    {
        var setup = new Fixture();
        if (stage is "prepared" or "applying")
            setup.Journal.AfterSave = session =>
            {
                if (session.Status == stage) setup.Clock.Advance(TimeSpan.FromSeconds(30));
            };
        else
            setup.Backend.BeforeWrite = () =>
            {
                if (stage == "external-candidate") setup.Backend.State = setup.Backend.State with { CurrentProfile = "candidate.icc" };
                setup.Clock.Advance(TimeSpan.FromSeconds(30));
                setup.Backend.BeforeWrite = null;
            };

        var expired = setup.Start();

        Assert.Empty(setup.Backend.Writes);
        Assert.Equal(stage == "external-candidate" ? "candidate.icc" : "original.icc", setup.Backend.State.CurrentProfile);
        Assert.Equal("reverted", expired.Status);
        Assert.Equal("preview_expired", expired.ReasonCode);
        Assert.False(expired.AssociationVerified);
        Assert.Equal(expired, setup.Journal.Last);
    }

    [Theory]
    [InlineData("native-write")]
    [InlineData("readback")]
    public void StartupExpirationAfterTheIccWriteRollsBackWithoutPublishingAReadyPreview(string stage)
    {
        var setup = new Fixture();
        if (stage == "native-write") setup.Backend.AfterWrite = () => setup.Clock.Advance(TimeSpan.FromSeconds(30));
        else setup.Backend.BeforeRead = () =>
        {
            if (setup.Backend.Writes.Count != 1) return;
            setup.Clock.Advance(TimeSpan.FromSeconds(30));
            setup.Backend.BeforeRead = null;
        };
        bool publishedReady = false;
        setup.Journal.AfterSave = session => publishedReady |= session.Status == "previewing";

        var expired = setup.Start();

        Assert.False(publishedReady);
        Assert.Equal("reverted", expired.Status);
        Assert.Equal("preview_expired", expired.ReasonCode);
        Assert.True(expired.AssociationVerified);
        Assert.Equal(new[] { "candidate.icc", "original.icc" }, setup.Backend.Writes);
        Assert.Equal("original.icc", setup.Backend.State.CurrentProfile);
    }

    [Fact]
    public void FailureToRecordAnUnstartedExpirationDoesNotRestoreOverAnExternalSelection()
    {
        var setup = new Fixture();
        setup.Journal.FailStatus = "reverted";
        setup.Backend.BeforeWrite = () =>
        {
            setup.Backend.State = setup.Backend.State with { CurrentProfile = "candidate.icc" };
            setup.Clock.Advance(TimeSpan.FromSeconds(30));
        };

        Assert.Throws<IOException>(() => setup.Start());

        Assert.Empty(setup.Backend.Writes);
        Assert.Equal("candidate.icc", setup.Backend.State.CurrentProfile);
        Assert.False(setup.Transaction.Session!.NeedsRecovery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateReadCrossingTheDeadlineRestoresOriginal(bool confirm)
    {
        var setup = new Fixture();
        setup.Start();
        setup.Backend.BeforeRead = () =>
        {
            setup.Clock.Advance(TimeSpan.FromSeconds(30));
            setup.Backend.BeforeRead = null;
        };

        var state = confirm ? setup.Transaction.Confirm() : setup.Transaction.Tick();

        Assert.Equal("reverted", state.Status);
        Assert.Equal("preview_expired", state.ReasonCode);
        Assert.Equal(new[] { "candidate.icc", "original.icc" }, setup.Backend.Writes);
        Assert.Equal("original.icc", setup.Backend.State.CurrentProfile);
    }

    [Fact] public void WindowOwnerDeathReverts()
    {
        var setup = new Fixture(); setup.Start();
        Assert.Equal("owner_exited", setup.Transaction.Tick(ownerAlive: false).ReasonCode);
        Assert.Equal("original.icc", setup.Backend.State.CurrentProfile);
    }

    [Fact] public void WindowExitDuringAssociationWriteRollsBackBeforeAdvertisingThePreview()
    {
        bool ownerAlive = true;
        var setup = new Fixture(() => ownerAlive);
        setup.Backend.AfterWrite = () => ownerAlive = false;
        bool published = false;
        setup.Journal.AfterSave = state => published |= state.Status == "previewing";

        var result = setup.Start();

        Assert.False(published);
        Assert.Equal("reverted", result.Status);
        Assert.Equal("owner_exited", result.ReasonCode);
        Assert.True(result.AssociationVerified);
        Assert.Equal(new[] { "candidate.icc", "original.icc" }, setup.Backend.Writes);
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

    [Fact] public void LegacyJournalRequiresItsOriginalTopology()
    {
        var setup = new Fixture(); setup.Start();
        setup.Backend.State = setup.Backend.State with { TopologyFingerprint = new string('E', 64) };
        var result = setup.Transaction.Tick();
        Assert.Equal("recovery-pending", result.Status);
        Assert.True(result.NeedsRecovery);
        Assert.Single(setup.Backend.Writes);
        setup.Backend.State = setup.Backend.State with { TopologyFingerprint = new string('A', 64), ActiveTopologySha256 = new string('D', 64) };
        var recovered = new ProfilePreviewTransaction(setup.Backend, setup.Journal).Recover(result);
        Assert.Equal("reverted", recovered.Status);
        Assert.Equal("original.icc", setup.Backend.State.CurrentProfile);
    }

    [Fact] public void ChangedLayoutEndsThePreviewAndRestoresOnTheSameDisplayPath()
    {
        var setup = new Fixture();
        setup.Backend.State = setup.Backend.State with { PersistenceKey = PersistenceKey };
        setup.Start();
        setup.Backend.State = setup.Backend.State with { TopologyFingerprint = new string('E', 64) };

        var result = setup.Transaction.Confirm();

        Assert.Equal("reverted", result.Status);
        Assert.Equal("display_state_changed", result.ReasonCode);
        Assert.True(result.AssociationVerified);
        Assert.Equal(new[] { "candidate.icc", "original.icc" }, setup.Backend.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActiveTopologyChangePreventsConfirmingTheProfile(bool hasIdentity)
    {
        var setup = new Fixture();
        string originalTopology = new('A', 64);
        setup.Backend.State = setup.Backend.State with
        {
            ActiveTopologySha256 = originalTopology,
            PersistenceKey = hasIdentity ? PersistenceKey : null
        };
        setup.Start();
        setup.Backend.State = setup.Backend.State with { ActiveTopologySha256 = new string('D', 64) };

        var result = setup.Transaction.Confirm();

        if (!hasIdentity)
        {
            Assert.Equal("recovery-pending", result.Status);
            Assert.Single(setup.Backend.Writes);
            setup.Backend.State = setup.Backend.State with { ActiveTopologySha256 = originalTopology };
            result = new ProfilePreviewTransaction(setup.Backend, setup.Journal).Recover(result);
        }
        Assert.Equal("reverted", result.Status);
        Assert.Equal(new[] { "candidate.icc", "original.icc" }, setup.Backend.Writes);
    }

    [Fact] public void ReconnectCanRestoreOnTheSameDisplayPathAfterTheLayoutChanges()
    {
        var setup = new Fixture();
        setup.Backend.State = setup.Backend.State with { PersistenceKey = PersistenceKey };
        setup.Start();
        setup.Backend.Disconnected = true;
        var pending = setup.Transaction.Tick();
        Assert.Equal("display_disconnected", pending.ReasonCode);
        Assert.True(pending.NeedsRecovery);
        Assert.Single(setup.Backend.Writes);
        setup.Backend.Disconnected = false;
        setup.Backend.State = setup.Backend.State with { TopologyFingerprint = new string('E', 64) };

        var recovered = new ProfilePreviewTransaction(setup.Backend, setup.Journal).Recover(pending);

        Assert.Equal("reverted", recovered.Status);
        Assert.Equal("original.icc", setup.Backend.State.CurrentProfile);
    }

    [Fact] public void RemoteSessionDefersProfileRecoveryUntilTheLocalDisplayReturns()
    {
        var setup = new Fixture();
        setup.Start();
        setup.Backend.State = setup.Backend.State with { IsRemoteSession = true };

        Assert.Equal("remote_session_blocked", ProfilePreviewPolicy.Plan(setup.Backend.State, "original.icc").ReasonCode);
        var pending = setup.Transaction.Tick();
        Assert.Equal("recovery-pending", pending.Status);
        Assert.Equal("remote_session_blocked", pending.ReasonCode);
        Assert.Single(setup.Backend.Writes);

        setup.Backend.State = setup.Backend.State with { IsRemoteSession = false };
        var restored = new ProfilePreviewTransaction(setup.Backend, setup.Journal).Recover(pending);
        Assert.Equal("reverted", restored.Status);
        Assert.Equal(new[] { "candidate.icc", "original.icc" }, setup.Backend.Writes);
    }

    [Theory]
    [InlineData("matched", "reverted", "interrupted_preview")]
    [InlineData("scope-changed", "recovery-pending", "recovery_target_changed")]
    [InlineData("file-changed", "recovery-pending", "profile_file_changed")]
    [InlineData("other-selection", "superseded", "external_profile_change")]
    public void InterruptedIccPreviewChecksTheSelectionAndFilesOnTheReassignedPath(string change, string status, string reason)
    {
        var setup = new Fixture();
        setup.Backend.State = setup.Backend.State with { PersistenceKey = PersistenceKey, ProfileDeviceSha256 = new string('F', 64) };
        var preview = setup.Start();
        var moved = setup.Backend.State with { DisplayId = "fedcba9876543210", TargetKey = "00000000:00000002:2", SourceId = 4 };
        setup.Backend.State = change switch
        {
            "scope-changed" => moved with { Scope = "system-wide" },
            "file-changed" => moved with { Profiles = [moved.Profiles[0], moved.Profiles[1] with { Sha256 = new string('D', 64) }] },
            "other-selection" => moved with { CurrentProfile = "other.icc" },
            _ => moved
        };
        setup.Backend.Remapped = true;

        var result = new ProfilePreviewTransaction(setup.Backend, setup.Journal).Recover(preview);

        Assert.Equal(preview.Original, result.Original);
        Assert.Equal(status, result.Status);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(change == "matched" ? new[] { "candidate.icc", "original.icc" } : ["candidate.icc"], setup.Backend.Writes);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("identity-unavailable")]
    [InlineData("target")]
    [InlineData("source")]
    [InlineData("scope")]
    [InlineData("cloned")]
    [InlineData("hdr")]
    public void RecoveryWaitsIfTheOriginalIndependentSdrPathCannotBeVerified(string change)
    {
        var setup = new Fixture();
        setup.Backend.State = setup.Backend.State with { PersistenceKey = PersistenceKey };
        setup.Start();
        var changed = setup.Backend.State with { TopologyFingerprint = new string('E', 64) };
        setup.Backend.State = change switch
        {
            "identity" => changed with { PersistenceKey = "edid:" + new string('E', 64) },
            "identity-unavailable" => changed with { PersistenceKey = null },
            "target" => changed with { TargetKey = "00000000:00000001:257" },
            "source" => changed with { SourceId = 1 },
            "scope" => changed with { Scope = "system-wide" },
            "cloned" => changed with { SharedSource = true },
            "hdr" => changed with { ExplicitSdr = false },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        var result = setup.Transaction.Revert();

        Assert.Equal("recovery-pending", result.Status);
        Assert.Equal("recovery_target_changed", result.ReasonCode);
        Assert.Single(setup.Backend.Writes);
    }

    [Fact] public void ChangedOriginalFilePreventsRestoringDifferentBytes()
    {
        var setup = new Fixture(); setup.Start();
        setup.Backend.State = setup.Backend.State with { Profiles =
            [new("original.icc", new string('E', 64), true), new("candidate.icc", new string('C', 64), true)] };
        Assert.Equal("original_profile_changed", setup.Transaction.Revert().ReasonCode);
        Assert.Single(setup.Backend.Writes);
    }

    [Fact] public void ReplacedCandidateFilePreventsRestoringOverAnUnverifiedSelection()
    {
        var setup = new Fixture(); setup.Start();
        setup.Backend.State = setup.Backend.State with { Profiles =
            [new("original.icc", new string('B', 64), true), new("candidate.icc", new string('E', 64), true)] };

        var result = setup.Transaction.Tick();

        Assert.Equal("recovery-pending", result.Status);
        Assert.Equal("profile_file_changed", result.ReasonCode);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedOnlyRecoveryDoesNotReadOrReplaceTheCurrentProfile(bool disconnected)
    {
        var setup = new Fixture();
        setup.Journal.AfterSave = session =>
        {
            if (session.Status == "prepared") throw new IOException("Simulated interruption after journaling.");
        };
        Assert.Throws<IOException>(() => setup.Start());
        var prepared = Assert.IsType<ProfilePreviewSession>(setup.Journal.Last);
        setup.Backend.State = setup.Backend.State with { CurrentProfile = "candidate.icc" };
        setup.Backend.Disconnected = disconnected;
        int reads = 0;
        setup.Backend.BeforeRead = () => reads++;

        var resolved = new ProfilePreviewTransaction(setup.Backend, setup.Journal).Recover(prepared);

        Assert.Empty(setup.Backend.Writes);
        Assert.Equal(0, reads);
        Assert.Equal("reverted", resolved.Status);
        Assert.Equal("preview_not_applied", resolved.ReasonCode);
        Assert.False(resolved.AssociationVerified);
        Assert.Equal("candidate.icc", setup.Backend.State.CurrentProfile);
        Assert.Equal(resolved, setup.Journal.Last);
    }

    [Fact]
    public void FailedPreWriteValidationPreservesACandidateSelectedByAnExternalApplication()
    {
        var setup = new Fixture();
        setup.Backend.BeforeWrite = () =>
        {
            setup.Backend.BeforeWrite = null;
            setup.Backend.State = setup.Backend.State with { CurrentProfile = "candidate.icc" };
            throw new ProfilePreviewException("display_state_changed", "Simulated pre-write validation failure.");
        };

        var error = Assert.Throws<ProfilePreviewException>(() => setup.Start());

        Assert.Equal("display_state_changed", error.Code);
        Assert.Empty(setup.Backend.Writes);
        Assert.Equal("reverted", setup.Journal.Last!.Status);
        Assert.Equal(error.Code, setup.Journal.Last.ReasonCode);
        Assert.False(setup.Journal.Last.AssociationVerified);
        Assert.Equal("candidate.icc", setup.Backend.State.CurrentProfile);
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BothOriginalAndCandidateMhc2ProfilesBlockNewPreviewsBeforeJournaling(int index)
    {
        var setup = new Fixture();
        var profiles = setup.Backend.State.Profiles.ToArray();
        profiles[index] = profiles[index] with { Mhc2Present = true };
        setup.Backend.State = setup.Backend.State with { Profiles = profiles };

        var plan = ProfilePreviewPolicy.Plan(setup.Backend.State, "candidate.icc");
        Assert.False(plan.CanPreview);
        Assert.Equal("mhc2_profile_blocked", plan.ReasonCode);
        Assert.Equal(plan.ReasonCode, Assert.Throws<ProfilePreviewException>(() => setup.Start()).Code);
        Assert.Null(setup.Journal.Last);
        Assert.Empty(setup.Backend.Writes);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void InterruptedAssociationCanRestoreTheRecordedOriginalEvenWhenMhc2IsDetected(int index, bool recordedTag)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dccwpp-mhc2-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var setup = new Fixture();
            var interrupted = setup.Start();
            var profiles = setup.Backend.State.Profiles.ToArray();
            profiles[index] = profiles[index] with { Mhc2Present = true };
            setup.Backend.State = setup.Backend.State with { Profiles = profiles };
            if (recordedTag) interrupted = interrupted with { Original = interrupted.Original with { Profiles = profiles } };
            var store = new ProfilePreviewStore(directory);
            store.Save(interrupted);

            var result = new ProfilePreviewTransaction(setup.Backend, store).Recover(store.Load(interrupted.Id));

            Assert.Equal("reverted", result.Status);
            Assert.Equal("original.icc", setup.Backend.State.CurrentProfile);
            Assert.Equal(new[] { "candidate.icc", "original.icc" }, setup.Backend.Writes);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact] public void JournalRoundTripsAndRejectsAnAlteredCandidateHash()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dccwpp-preview-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var setup = new Fixture();
            setup.Backend.State = setup.Backend.State with { PersistenceKey = PersistenceKey, ActiveTopologySha256 = new string('D', 64) };
            var session = setup.Start(); var store = new ProfilePreviewStore(directory);
            store.Save(session); Assert.Equal(session.Id, store.Load(session.Id).Id);
            Assert.Equal(PersistenceKey, store.Load(session.Id).Original.PersistenceKey);
            Assert.Equal(setup.Backend.State.ActiveTopologySha256, store.Load(session.Id).Original.ActiveTopologySha256);
            Assert.Throws<InvalidDataException>(() => store.Save(session with { Original = session.Original with { PersistenceKey = "invalid" } }));
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
        public Fixture(Func<bool>? isOwnerAlive = null) => Transaction = new(Backend, Journal, Clock, isOwnerAlive);
        public ProfilePreviewSession Start() => Transaction.Begin(Guid.NewGuid(), Backend.State.DisplayId, "candidate.icc", 30);
    }
    private sealed class FakeBackend : IProfileAssociationBackend
    {
        public ProfileAssociationState State { get; set; } = new("0123456789abcdef", "00000000:00000001:256", 0,
            new string('A', 64), "original.icc", "current-user", true, false,
            [new("original.icc", new string('B', 64), true), new("candidate.icc", new string('C', 64), true)]);
        public List<string> Writes { get; } = []; public Action? BeforeWrite { get; set; }
        public Action? AfterWrite { get; set; }
        public bool FailFirstAfterWrite { get; set; }
        public bool Disconnected { get; set; }
        public bool Remapped { get; set; }
        public Action? BeforeRead { get; set; }
        public ProfileAssociationState Read(string id)
        {
            BeforeRead?.Invoke();
            if (Disconnected || (Remapped && id != State.DisplayId))
                throw new ProfilePreviewException("display_disconnected", "Simulated disconnected display.");
            return State;
        }
        public ProfileAssociationState ReadRecoveryTarget(ProfileAssociationState original) => Read(Remapped ? State.DisplayId : original.DisplayId);
        public bool TrySetDefault(ProfileAssociationState expected, string profile, string hash, Func<bool>? canWrite = null)
        {
            BeforeWrite?.Invoke();
            if (canWrite?.Invoke() == false) return false;
            Writes.Add(profile); State = State with { CurrentProfile = profile };
            AfterWrite?.Invoke();
            if (FailFirstAfterWrite && Writes.Count == 1) throw new IOException("Injected native failure.");
            return true;
        }
    }
    private sealed class FakeJournal : IProfilePreviewJournal
    {
        public ProfilePreviewSession? Last { get; private set; } public string? FailStatus { get; set; }
        public Action<ProfilePreviewSession>? AfterSave { get; set; }
        public void Save(ProfilePreviewSession session)
        {
            if (session.Status == FailStatus) throw new IOException("Injected journal failure.");
            Last = session;
            AfterSave?.Invoke(session);
        }
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
