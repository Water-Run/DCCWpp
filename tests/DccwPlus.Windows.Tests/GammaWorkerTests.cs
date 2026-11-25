using System.Diagnostics;
using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Presets;
using DccwPlus.Core.Storage;
using DccwPlus.Windows;
using Xunit;

namespace DccwPlus.Windows.Tests;

public sealed class GammaWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dccwpp-worker-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GammaWorkerChecksTheOwnerAtTheDriverWrite(bool windowOwned)
    {
        using var owner = StartDisposableOwner();
        var backend = new SimulatedGammaBackend();
        var store = new GammaCalibrationStore(_root);
        var id = Guid.NewGuid();
        var receipt = Guid.NewGuid();
        var preset = CalibrationPreset.Create("Owner fixture", new() { RedGain = .9 });
        WriteRequest(store, new(id, backend.State.DisplayId, preset, 30, windowOwned ? owner.Id : null,
            windowOwned ? owner.StartTime.ToUniversalTime().Ticks : null));
        backend.BeforeWrite = () =>
        {
            backend.BeforeWrite = null;
            StopDisposableOwner(owner);
            if (!windowOwned) AtomicFile.Write(Path.Combine(store.DirectoryFor(id), "command.json"),
                JsonSerializer.SerializeToUtf8Bytes(new GammaPreviewCommand(Guid.NewGuid(), "revert"), ProfilePreviewStore.Json));
        };

        try
        {
            int exitCode = GammaPreviewHost.Run(_root, id, () => backend, receipt);
            Assert.Equal(windowOwned ? 0 : 2, backend.Writes);
            Assert.Equal(windowOwned ? 4 : 0, exitCode);
            var resolved = store.Load(id);
            Assert.Equal("reverted", resolved.Status);
            Assert.Equal(windowOwned ? "owner_exited" : "user_reverted", resolved.ReasonCode);
            Assert.Equal(!windowOwned, resolved.CurveReadbackVerified);
            Assert.True(backend.State.Ramp.Matches(GammaRamp.Identity()));
            if (windowOwned) Assert.Equal("owner_exited", ProfilePreviewStore.Read<ProfileWorkerError>(
                PreviewWorkerFiles.ErrorPath(store.DirectoryFor(id), receipt)).Code);
        }
        finally { StopDisposableOwner(owner); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProfileWorkerChecksTheOwnerAtTheAssociationWrite(bool windowOwned)
    {
        using var owner = StartDisposableOwner();
        var backend = new SimulatedProfileBackend();
        var store = new ProfilePreviewStore(_root);
        var id = Guid.NewGuid();
        var receipt = Guid.NewGuid();
        string directory = store.DirectoryFor(id);
        AtomicFile.Write(Path.Combine(directory, "request.json"), JsonSerializer.SerializeToUtf8Bytes(
            new ProfilePreviewRequest(id, backend.State.DisplayId, "candidate.icc", 30, windowOwned ? owner.Id : null,
                windowOwned ? owner.StartTime.ToUniversalTime().Ticks : null), ProfilePreviewStore.Json));
        backend.BeforeWrite = () =>
        {
            backend.BeforeWrite = null;
            StopDisposableOwner(owner);
            if (!windowOwned) store.Request(id, "revert");
        };

        try
        {
            int exitCode = ProfilePreviewHost.Run(_root, id, receipt, () => backend);
            Assert.Equal(windowOwned ? 0 : 2, backend.Writes);
            Assert.Equal(windowOwned ? 4 : 0, exitCode);
            var resolved = store.Load(id);
            Assert.Equal("reverted", resolved.Status);
            Assert.Equal(windowOwned ? "owner_exited" : "user_reverted", resolved.ReasonCode);
            Assert.Equal(!windowOwned, resolved.AssociationVerified);
            Assert.Equal("original.icc", backend.State.CurrentProfile);
            if (windowOwned) Assert.Equal("owner_exited", ProfilePreviewStore.Read<ProfileWorkerError>(
                PreviewWorkerFiles.ErrorPath(directory, receipt)).Code);
        }
        finally { StopDisposableOwner(owner); }
    }

    [Fact]
    public void LaunchWaitsForAnOwnedExpiredPreviewAndRejectsAnAbandonedJournal()
    {
        var store = new GammaCalibrationStore(_root);
        var backend = new SimulatedGammaBackend();
        var id = Guid.NewGuid();
        var active = new GammaCalibrationTransaction(backend, store).Begin(id,
            CalibrationPreset.Create("Initial", new() { RedGain = .9 }), backend.State.DisplayId, 60);
        string directory = store.DirectoryFor(id);
        GammaCalibrationSession? Read(bool exited) => PreviewWorkerFiles.ReadReadySession(directory, () => store.Find(id),
            state => state.NeedsRecovery && state.Status != "recovery-pending",
            state => state.Status == "previewing" ? state.ExpiresAt : null, launchExited: exited);

        using (var worker = PreviewWorkerFiles.TryLock(directory, "worker"))
        {
            Assert.NotNull(worker);
            Assert.Equal("previewing", Read(exited: false)?.Status);
            store.Save(active with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
            Assert.Null(Read(exited: true)); // A joined launcher may exit while the original worker rolls back.
        }

        Assert.Equal("recovery_required", Assert.Throws<ProfilePreviewException>(() => Read(exited: true)).Code);
        store.Save(active with { Status = "reverted", ReasonCode = "preview_expired" });
        Assert.Null(Read(exited: false));
        Assert.Equal("preview_expired", Read(exited: true)?.ReasonCode);
    }

    [Fact]
    public async Task DuplicateWorkerAndSimultaneousClientsShareOneSessionWithoutLosingCommands()
    {
        var store = new GammaCalibrationStore(_root);
        var preset = CalibrationPreset.Create("Initial", new() { RedGain = .9 });
        var backend = new SimulatedGammaBackend();
        string displayId = backend.State.DisplayId;
        Guid id = Guid.NewGuid();
        string requestPath = WriteRequest(store, new(id, displayId, preset, 30, AllowRetries: true));
        var reserved = File.ReadAllBytes(requestPath);
        var worker = Task.Run(() => GammaPreviewHost.Run(_root, id, () => backend, attemptId: Guid.NewGuid()));

        try
        {
            var preview = await WaitForPreviewAsync(store, id, worker);
            Assert.Equal(1, backend.Writes);
            var recovery = new PreviewRecoveryService(_root);
            Assert.Equal(id, Assert.Single(recovery.List()).SessionId);
            Assert.Equal("preview_busy", recovery.Recover().Reason);
            var startup = new CalibrationBindingService(_root);
            Assert.Equal("preview_busy", startup.RunStartup(true, "sign_in", () => null).Reason);
            Assert.Equal("sign_in", startup.LastStartupReport()?.Trigger);
            Assert.Equal("previewing", store.Load(id).Status);
            Assert.Equal(1, backend.Writes);
            int duplicate = await Task.Run(() => GammaPreviewHost.Run(_root, id,
                () => throw new InvalidOperationException("A second backend must not open a session."), attemptId: Guid.NewGuid()));
            Assert.Equal(10, duplicate);
            var a = new GammaPreviewClient(_root, "unused");
            var b = new GammaPreviewClient(_root, "unused");
            Assert.True(a.Observe(id).WorkerRunning);
            var observer = new PreviewSessionObserver(_root);
            var active = await observer.ObserveCalibrationAsync(id, 0);
            Assert.Equal("in_progress", active.Outcome);
            Assert.Equal(preview.ExpiresAt, active.Session.ExpiresAt);
            Assert.Equal(preview.ExpiresAt, a.FindByRequestId(id, displayId, preset.Id, 30)!.ExpiresAt);

            var firstEdit = preset with { Calibration = new() { RedGain = .8 } };
            var secondEdit = preset with { Calibration = new() { BlueGain = .8 } };
            var updates = await Task.WhenAll(a.CommandAsync(id, "update", firstEdit), b.CommandAsync(id, "update", secondEdit));
            Assert.Contains(updates, s => s.Preset.Calibration == firstEdit.Calibration);
            Assert.Contains(updates, s => s.Preset.Calibration == secondEdit.Calibration);
            Assert.All(updates, s => Assert.Equal(preview.ExpiresAt, s.ExpiresAt));
            Assert.Equal(preview.ExpiresAt, (await a.CommandAsync(id, "heartbeat")).ExpiresAt);
            Assert.Equal(preview.ExpiresAt, (await b.CommandAsync(id, "review")).ExpiresAt);
            Assert.Equal(3, backend.Writes);

            var observing = observer.ObserveCalibrationAsync(id, 4);
            Assert.False(observing.IsCompleted);
            Assert.Equal("reverted", (await a.CommandAsync(id, "revert")).Status);
            var observed = await observing;
            Assert.Equal("finished", observed.Outcome);
            Assert.Equal("reverted", observed.Session.Status);
            Assert.Equal(0, await worker.WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.Equal(4, backend.Writes);
            Assert.True(backend.State.Ramp.Matches(GammaRamp.Identity()));
            Assert.Equal(0, GammaPreviewHost.Run(_root, id, () => throw new InvalidOperationException("A finished session never reopens.")));
            Assert.Equal("reverted", b.FindByRequestId(id, displayId, preset.Id, 30)!.Status);
            Assert.Equal(reserved, File.ReadAllBytes(requestPath));
        }
        finally
        {
            if (!worker.IsCompleted && store.Find(id)?.Status == "previewing")
                await new GammaPreviewClient(_root, "unused").CommandAsync(id, "revert");
            await worker.WaitAsync(TimeSpan.FromSeconds(35));
        }
    }

    [Fact]
    public async Task WindowReviewAcknowledgesPendingValuesAndKeepsTheComparisonDeadline()
    {
        using var owner = Process.GetCurrentProcess();
        var backend = new SimulatedGammaBackend();
        var store = new GammaCalibrationStore(_root);
        var client = new GammaPreviewClient(_root, "unused");
        var id = Guid.NewGuid();
        var preset = CalibrationPreset.Create("Window", new() { RedGain = .9 });
        var edits = preset with { Calibration = new() { Gamma = 2.42, BlueGain = .91 } };
        WriteRequest(store, new(id, backend.State.DisplayId, preset, 60, owner.Id, owner.StartTime.ToUniversalTime().Ticks));
        var worker = Task.Run(() => GammaPreviewHost.Run(_root, id, () => backend));

        try
        {
            var preview = await WaitForPreviewAsync(store, id, worker);
            var reviewed = await client.CommandAsync(id, "review", edits);
            Assert.True(reviewed.IsReviewing);
            Assert.Equal(edits, reviewed.Preset);
            Assert.True(backend.State.Ramp.Matches(reviewed.Candidate));
            Assert.True(reviewed.ExpiresAt < preview.ExpiresAt);

            var original = await client.CommandAsync(id, "update", edits, showOriginal: true);
            Assert.True(original.ShowingOriginal);
            Assert.True(backend.State.Ramp.Matches(GammaRamp.Identity()));
            Assert.Equal(reviewed.ExpiresAt, original.ExpiresAt);
            Assert.Equal(reviewed.ExpiresAt, (await client.CommandAsync(id, "review")).ExpiresAt);

            var resumed = await client.CommandAsync(id, "edit");
            Assert.False(resumed.IsReviewing);
            Assert.False(resumed.ShowingOriginal);
            Assert.True(backend.State.Ramp.Matches(reviewed.Candidate));
        }
        finally
        {
            if (!worker.IsCompleted && store.Find(id)?.Status == "previewing") await client.CommandAsync(id, "revert");
            await worker.WaitAsync(TimeSpan.FromSeconds(35));
        }
        Assert.Equal("reverted", store.Load(id).Status);
        Assert.True(backend.State.Ramp.Matches(GammaRamp.Identity()));
    }

    [Fact]
    public async Task InlinePreviewCanBeRetriedAndEditedWithoutExtendingItsDeadline()
    {
        var backend = new SimulatedGammaBackend();
        var store = new GammaCalibrationStore(_root);
        var client = new GammaPreviewClient(_root, "unused");
        var id = Guid.NewGuid();
        string displayId = backend.State.DisplayId;
        var preset = CalibrationPreset.Create("Inline", new() { Gamma = 2.35 });
        string requestPath = WriteRequest(store, new(id, displayId, preset, 30, AllowRetries: true, InlineParameters: true));
        var request = File.ReadAllBytes(requestPath);
        var worker = Task.Run(() => GammaPreviewHost.Run(_root, id, () => backend));

        try
        {
            var preview = await WaitForPreviewAsync(store, id, worker);
            var retry = CalibrationPreset.Create("Localized name", preset.Calibration);
            Assert.Equal(preview.Id, (await client.BeginAsync(displayId, retry, requestId: id, inlineParameters: true)).Id);
            var conflict = await Assert.ThrowsAsync<ProfilePreviewException>(() => client.BeginAsync(displayId,
                retry with { Calibration = new() { Gamma = 2.25 } }, requestId: id, inlineParameters: true));
            Assert.Equal("request_id_conflict", conflict.Code);

            var secondClient = new GammaPreviewClient(_root, "unused");
            await Task.WhenAll(
                client.EditAsync(id, p => p with { Calibration = p.Calibration with { RedGain = .9 } }, false),
                secondClient.EditAsync(id, p => p with { Calibration = p.Calibration with { BlueGain = .8 } }, false));
            var edited = store.Load(id);
            Assert.Equal(new SdrCalibration { Gamma = 2.35, RedGain = .9, BlueGain = .8 }, edited.Preset.Calibration);
            var original = await client.EditAsync(id, current => current, true);
            Assert.True(original.ShowingOriginal);
            Assert.True(backend.State.Ramp.Matches(GammaRamp.Identity()));
            var adjusted = await client.EditAsync(id, current => current, false);
            Assert.False(adjusted.ShowingOriginal);
            Assert.True(backend.State.Ramp.Matches(edited.Candidate));
            Assert.All(new[] { edited, original, adjusted }, session => Assert.Equal(preview.ExpiresAt, session.ExpiresAt));
            Assert.Empty(new PresetStore(_root).List().Presets);

            Assert.Equal("reverted", (await client.CommandAsync(id, "revert")).Status);
            Assert.Equal(0, await worker.WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.True(backend.State.Ramp.Matches(GammaRamp.Identity()));
            Assert.Equal(request, File.ReadAllBytes(requestPath));
        }
        finally
        {
            if (!worker.IsCompleted && store.Find(id)?.Status == "previewing") await client.CommandAsync(id, "revert");
            await worker.WaitAsync(TimeSpan.FromSeconds(35));
        }
    }

    [Fact]
    public async Task DuplicateProfileWorkerKeepsTheOriginalPreviewAndDeadline()
    {
        var profile = new SimulatedProfileBackend();
        var store = new ProfilePreviewStore(_root);
        var client = new ProfilePreviewClient(_root, "unused");
        var id = Guid.NewGuid();
        string displayId = profile.State.DisplayId;
        string path = Path.Combine(store.DirectoryFor(id), "request.json");
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(
            new ProfilePreviewRequest(id, displayId, "candidate.icc", 30, AllowRetries: true), ProfilePreviewStore.Json);
        AtomicFile.Write(path, request, overwrite: false);
        var worker = Task.Run(() => ProfilePreviewHost.Run(_root, id, Guid.NewGuid(), () => profile));

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!worker.IsCompleted && store.Find(id) is not { Status: "previewing" })
                await Task.Delay(25, deadline.Token);
            Assert.False(worker.IsCompleted, "The simulated profile worker exited before previewing.");
            Assert.Equal("candidate.icc", profile.State.CurrentProfile);
            Assert.Equal(10, await Task.Run(() => ProfilePreviewHost.Run(_root, id, Guid.NewGuid(),
                () => throw new InvalidOperationException("A second backend must not open a session."))));
            var expiresAt = store.Load(id).ExpiresAt;
            Assert.Equal(expiresAt, (await client.BeginAsync(displayId, "candidate.icc", requestId: id)).ExpiresAt);
            var observer = new PreviewSessionObserver(_root);
            var timeout = await observer.ObserveProfileAsync(id, 1);
            Assert.Equal("wait_timeout", timeout.Outcome);
            Assert.Equal("previewing", timeout.Session.Status);
            Assert.Equal(expiresAt, timeout.Session.ExpiresAt);
            Assert.Equal("candidate.icc", profile.State.CurrentProfile);
            Assert.Equal("reverted", (await client.CommandAsync(id, "revert")).Status);
            Assert.Equal(0, await worker.WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.Equal("finished", (await observer.ObserveProfileAsync(id, 0)).Outcome);
            Assert.Equal(0, ProfilePreviewHost.Run(_root, id, Guid.NewGuid(),
                () => throw new InvalidOperationException("A finished session must not reopen.")));
            Assert.Equal("reverted", (await client.BeginAsync(displayId, "CANDIDATE.ICC", requestId: id)).Status);
            Assert.Equal("original.icc", profile.State.CurrentProfile);
            Assert.Equal(request, File.ReadAllBytes(path));
        }
        finally
        {
            if (!worker.IsCompleted && store.Find(id)?.Status == "previewing") store.Request(id, "revert");
            await worker.WaitAsync(TimeSpan.FromSeconds(35));
        }
    }

    [Fact]
    public async Task ProfileWorkerReportsStartupExpirationAndKeepsTheRequestReplayable()
    {
        var clock = new ProfileWorkerClock();
        var backend = new SimulatedProfileBackend { BeforeWrite = () => clock.Elapsed = TimeSpan.FromSeconds(30) };
        var store = new ProfilePreviewStore(_root);
        var id = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        string directory = store.DirectoryFor(id);
        AtomicFile.Write(Path.Combine(directory, "request.json"), JsonSerializer.SerializeToUtf8Bytes(
            new ProfilePreviewRequest(id, backend.State.DisplayId, "candidate.icc", 30, AllowRetries: true), ProfilePreviewStore.Json));

        Assert.Equal(4, ProfilePreviewHost.Run(_root, id, attempt, () => backend, clock));

        string errorPath = PreviewWorkerFiles.ErrorPath(directory, attempt);
        Assert.Equal("preview_expired", ProfilePreviewStore.Read<ProfileWorkerError>(errorPath).Code);
        Assert.Equal("original.icc", backend.State.CurrentProfile);
        var client = new ProfilePreviewClient(_root, "unused");
        var replay = await client.BeginAsync(backend.State.DisplayId, "candidate.icc", requestId: id);
        Assert.Equal("reverted", replay.Status);
        Assert.Equal("preview_expired", replay.ReasonCode);
        Assert.False(replay.AssociationVerified);
        Assert.Empty(store.Incomplete());
    }

    [Fact]
    public void ProfileWorkerRejectsMhc2BeforeCreatingACalibrationJournal()
    {
        var backend = new SimulatedProfileBackend();
        backend.State = backend.State with { Profiles = [backend.State.Profiles[0], backend.State.Profiles[1] with { Mhc2Present = true }] };
        var store = new ProfilePreviewStore(_root);
        var id = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        string directory = store.DirectoryFor(id);
        AtomicFile.Write(Path.Combine(directory, "request.json"), JsonSerializer.SerializeToUtf8Bytes(
            new ProfilePreviewRequest(id, backend.State.DisplayId, "candidate.icc", 30), ProfilePreviewStore.Json));

        Assert.Equal(4, ProfilePreviewHost.Run(_root, id, attempt, () => backend));

        var error = ProfilePreviewStore.Read<ProfileWorkerError>(PreviewWorkerFiles.ErrorPath(directory, attempt));
        Assert.Equal("mhc2_profile_blocked", error.Code);
        Assert.Contains("MHC2", error.Message);
        Assert.Null(store.Find(id));
        Assert.Equal("original.icc", backend.State.CurrentProfile);
    }

    [Fact]
    public async Task ProfileWorkerRetainsOwnershipThroughErrorRecovery()
    {
        var backend = new SimulatedProfileBackend();
        var store = new ProfilePreviewStore(_root);
        var id = Guid.NewGuid();
        string path = Path.Combine(store.DirectoryFor(id), "request.json");
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(
            new ProfilePreviewRequest(id, backend.State.DisplayId, "candidate.icc", 30), ProfilePreviewStore.Json));
        var restoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int reads = 0;
        backend.BeforeRead = () =>
        {
            switch (++reads)
            {
                case 2: throw new IOException("The first candidate verification could not read the association.");
                case 3: throw new ProfilePreviewException("display_disconnected", "The display was temporarily disconnected.");
                case 4:
                    restoring.SetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release restoration.");
                    break;
            }
        };
        var worker = Task.Run(() => ProfilePreviewHost.Run(_root, id, Guid.NewGuid(), () => backend));

        try
        {
            await restoring.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var observation = await new PreviewSessionObserver(_root).ObserveProfileAsync(id, 0);
            Assert.Equal("recovering", observation.Session.Status);
            Assert.Equal("in_progress", observation.Outcome);
        }
        finally
        {
            release.Set();
            await worker.WaitAsync(TimeSpan.FromSeconds(8));
        }

        Assert.Equal(4, await worker);
        Assert.Equal("reverted", store.Load(id).Status);
        Assert.Equal("original.icc", backend.State.CurrentProfile);
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("revert")]
    public async Task ProfileCommandDoesNotQueueForAnAbandonedSession(string action)
    {
        var backend = new SimulatedProfileBackend();
        var store = new ProfilePreviewStore(_root);
        var id = Guid.NewGuid();
        var session = new ProfilePreviewTransaction(backend, store).Begin(id, backend.State.DisplayId, "candidate.icc", 30);
        var client = new ProfilePreviewClient(_root, "unused");

        var error = await Assert.ThrowsAsync<ProfilePreviewException>(() => client.CommandAsync(id, action));
        Assert.Equal("recovery_required", error.Code);
        Assert.False(store.Requested(id, action));
        Assert.Equal("previewing", store.Load(id).Status);

        store.Save(session with { Status = "recovery-pending", ReasonCode = "display_disconnected" });
        Assert.Equal("display_disconnected", (await client.CommandAsync(id, action)).ReasonCode);
        Assert.False(store.Requested(id, action));
    }

    [Fact]
    public async Task ProfileCommandDetectsAWorkerExitAfterSubmission()
    {
        var backend = new SimulatedProfileBackend();
        var store = new ProfilePreviewStore(_root);
        var id = Guid.NewGuid();
        new ProfilePreviewTransaction(backend, store).Begin(id, backend.State.DisplayId, "candidate.icc", 30);
        var client = new ProfilePreviewClient(_root, "unused");
        Task<ProfilePreviewSession> confirm;
        using (var worker = PreviewWorkerFiles.TryLock(store.DirectoryFor(id), "worker"))
        {
            Assert.NotNull(worker);
            confirm = client.CommandAsync(id, "confirm");
            Assert.True(store.Requested(id, "confirm"));
            Assert.False(confirm.IsCompleted);
        }

        var error = await Assert.ThrowsAsync<ProfilePreviewException>(() => confirm.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("recovery_required", error.Code);
        Assert.Equal("previewing", store.Load(id).Status);
    }

    [Fact]
    public async Task BulkRecoveryKeepsAnIccRollbackObservableWithoutAcceptingConfirmation()
    {
        var store = new ProfilePreviewStore(_root);
        var backend = new SimulatedProfileBackend();
        var id = Guid.NewGuid();
        new ProfilePreviewTransaction(backend, store).Begin(id, backend.State.DisplayId, "candidate.icc", 30);
        var restoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        backend.BeforeRead = () =>
        {
            restoring.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException();
        };
        var service = new PreviewRecoveryService(_root);
        var recovery = Task.Run(() =>
        {
            using var gate = DisplayMutationLock.Acquire();
            return service.RecoverLocked(new SimulatedGammaBackend(), backend);
        });

        var observer = new PreviewSessionObserver(_root);
        Task<ProfilePreviewSession>? confirm = null;
        try
        {
            await restoring.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var observation = await observer.ObserveProfileAsync(id, 0);
            Assert.Equal("recovering", observation.Session.Status);
            Assert.Equal("in_progress", observation.Outcome);
            Assert.Equal("preview_busy", new CalibrationBindingService(_root).RunStartup(true, "resume", () => null).Reason);
            confirm = new ProfilePreviewClient(_root, "unused").CommandAsync(id, "confirm");
            Assert.False(confirm.IsCompleted);
            Assert.False(store.Requested(id, "confirm"));
        }
        finally
        {
            release.Set();
            await recovery.WaitAsync(TimeSpan.FromSeconds(8));
        }
        Assert.Equal("recovery_finished", (await recovery).Reason);
        Assert.Equal("reverted", (await confirm!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal("finished", (await observer.ObserveProfileAsync(id, 0)).Outcome);
        Assert.Equal("original.icc", backend.State.CurrentProfile);
    }

    [Fact]
    public async Task BulkRecoveryLeavesAnOwnedSessionAloneAndReportsBusy()
    {
        var store = new GammaCalibrationStore(_root);
        var gamma = new SimulatedGammaBackend();
        var id = Guid.NewGuid();
        new GammaCalibrationTransaction(gamma, store).Begin(id,
            CalibrationPreset.Create("Initial", new() { RedGain = .9 }), gamma.State.DisplayId, 30);
        var service = new PreviewRecoveryService(_root);
        PreviewRecoveryReport report;
        using (var worker = PreviewWorkerFiles.TryLock(store.DirectoryFor(id), "worker"))
        {
            Assert.NotNull(worker);
            using (DisplayMutationLock.Acquire())
                report = service.RecoverLocked(gamma, new SimulatedProfileBackend());
            Assert.Equal("preview_busy", report.Reason);
            Assert.Equal("preview_busy", Assert.Single(report.Sessions).ReasonCode);
            Assert.Equal("preview_busy", new CalibrationBindingService(_root).RunStartup(true, "resume", () => null).Reason);
            Assert.Equal("previewing", store.Load(id).Status);
            Assert.Equal(1, gamma.Writes);
        }

        using (DisplayMutationLock.Acquire())
            report = service.RecoverLocked(gamma, new SimulatedProfileBackend());
        Assert.Equal("recovery_finished", report.Reason);
        Assert.Equal("finished", (await new PreviewSessionObserver(_root).ObserveCalibrationAsync(id, 0)).Outcome);
        Assert.True(gamma.State.Ramp.Matches(GammaRamp.Identity()));
    }

    [Fact]
    public async Task CommonRecoveryRestoresBothJournalsAndPreservesTheProfileRequest()
    {
        var gamma = new SimulatedGammaBackend();
        var profile = new SimulatedProfileBackend();
        var gammaStore = new GammaCalibrationStore(_root);
        var profileStore = new ProfilePreviewStore(_root);
        var gammaId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        new GammaCalibrationTransaction(gamma, gammaStore).Begin(gammaId,
            CalibrationPreset.Create("Initial", new() { RedGain = .9 }), gamma.State.DisplayId, 30);
        new ProfilePreviewTransaction(profile, profileStore).Begin(profileId, profile.State.DisplayId, "candidate.icc", 30);
        string requestPath = Path.Combine(profileStore.DirectoryFor(profileId), "request.json");
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(
            new ProfilePreviewRequest(profileId, profile.State.DisplayId, "candidate.icc", 30), ProfilePreviewStore.Json);
        AtomicFile.Write(requestPath, request, overwrite: false);

        var service = new PreviewRecoveryService(_root);
        var observer = new PreviewSessionObserver(_root);
        Assert.Equal("recovery_required", (await observer.ObserveProfileAsync(profileId, 0)).Outcome);
        var pending = service.List();
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, result => result.SessionId == gammaId && result.Kind == "calibration");
        Assert.Contains(pending, result => result.SessionId == profileId && result.Kind == "profile");
        profile.Disconnected = true;
        PreviewRecoveryReport report;
        using (DisplayMutationLock.Acquire())
            report = service.RecoverLocked(gamma, profile);

        Assert.Equal("recovery_required", report.Reason);
        Assert.Equal(2, report.Sessions.Count);
        Assert.Equal(profileId, Assert.Single(service.List()).SessionId);
        Assert.True(gamma.State.Ramp.Matches(GammaRamp.Identity()));
        Assert.Equal("candidate.icc", profile.State.CurrentProfile);

        profile.Disconnected = false;
        profile.ChangeLayout();
        using (DisplayMutationLock.Acquire())
            report = service.RecoverLocked(gamma, profile);
        Assert.Equal("recovery_finished", report.Reason);
        Assert.Equal("reverted", Assert.Single(report.Sessions).Status);
        Assert.Empty(service.List());
        Assert.Equal("original.icc", profile.State.CurrentProfile);

        var terminal = await new ProfilePreviewClient(_root, "unused").RecoverAsync(profileId);
        Assert.Equal("reverted", terminal.Status);
        Assert.Equal(request, File.ReadAllBytes(requestPath));
    }

    [Fact]
    public async Task RecoveryUsesTheSavedSessionAndKeepsTheRequest()
    {
        var store = new GammaCalibrationStore(_root);
        var preset = CalibrationPreset.Create("Original", new() { BlueGain = .85 });
        var backend = new SimulatedGammaBackend();
        Guid id = Guid.NewGuid();
        var request = new GammaPreviewRequest(id, backend.State.DisplayId, preset, 30, AllowRetries: true);
        string path = WriteRequest(store, request);
        byte[] original = File.ReadAllBytes(path);
        new GammaCalibrationTransaction(backend, store).Begin(id, preset, request.DisplayId, 30);

        var client = new GammaPreviewClient(_root, "unused");
        Assert.False(client.Observe(id).WorkerRunning);
        Assert.Equal("previewing", client.Status(id).Status);
        var observed = await new PreviewSessionObserver(_root).ObserveCalibrationAsync(id, 0);
        Assert.Equal("recovery_required", observed.Outcome);
        Assert.Equal("previewing", observed.Session.Status);
        var error = await Assert.ThrowsAsync<ProfilePreviewException>(() => client.CommandAsync(id, "confirm"));
        Assert.Equal("recovery_required", error.Code);
        Assert.False(File.Exists(Path.Combine(store.DirectoryFor(id), "command.json")));

        Assert.Equal(0, GammaPreviewHost.Run(_root, id, () => backend, attemptId: Guid.NewGuid(), recover: true));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal("reverted", store.Load(id).Status);
        Assert.True(backend.State.Ramp.Matches(GammaRamp.Identity()));
    }

    [Fact]
    public async Task ACommandWaitingForAcknowledgementDetectsAnExitedWorker()
    {
        var backend = new SimulatedGammaBackend();
        var store = new GammaCalibrationStore(_root);
        var client = new GammaPreviewClient(_root, "unused");
        var id = Guid.NewGuid();
        new GammaCalibrationTransaction(backend, store).Begin(id,
            CalibrationPreset.Create("Interrupted", new() { RedGain = .9 }), backend.State.DisplayId, 30);
        string directory = store.DirectoryFor(id);
        Task<GammaCalibrationSession> cancelling;
        using (var worker = PreviewWorkerFiles.TryLock(directory, "worker"))
        {
            Assert.NotNull(worker);
            cancelling = client.CommandAsync(id, "revert");
            Assert.True(File.Exists(Path.Combine(directory, "command.json")));
            Assert.False(cancelling.IsCompleted);
        }

        var error = await Assert.ThrowsAsync<ProfilePreviewException>(() => cancelling.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("recovery_required", error.Code);
        Assert.True(File.Exists(Path.Combine(directory, "worker.lock")));
        Assert.False(client.Observe(id).WorkerRunning);
        Assert.Equal("previewing", store.Load(id).Status);
        Assert.Equal(1, backend.Writes);
    }

    [Fact]
    public async Task AnExitedWorkerDoesNotHideAConfirmedOutcomeAwaitingBindingCompletion()
    {
        var backend = new SimulatedGammaBackend();
        var store = new GammaCalibrationStore(_root);
        var client = new GammaPreviewClient(_root, "unused");
        var id = Guid.NewGuid();
        var preview = new GammaCalibrationTransaction(backend, store).Begin(id,
            CalibrationPreset.Create("Confirmed", new() { RedGain = .9 }), backend.State.DisplayId, 30);
        var observer = new PreviewSessionObserver(_root);

        Task<GammaCalibrationSession> confirming;
        using (var worker = PreviewWorkerFiles.TryLock(store.DirectoryFor(id), "worker"))
        {
            Assert.NotNull(worker);
            confirming = client.CommandAsync(id, "confirm");
            Assert.False(confirming.IsCompleted);
            store.Save(preview with
            {
                Status = "confirmed", BindingStatus = "pending", ProfileCommitStarted = true,
                CurveReadbackVerified = true, InstalledProfile = $"DCCWPlus-{id:N}.icc"
            });
            Assert.Equal("in_progress", (await observer.ObserveCalibrationAsync(id, 0)).Outcome);
        }

        var result = await confirming.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("confirmed", result.Status);
        Assert.True(result.NeedsBindingCompletion);
        Assert.Equal("recovery_required", (await observer.ObserveCalibrationAsync(id, 0)).Outcome);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ConfirmReportsTheCommittedJournalWhenTheAcknowledgementFails(bool committed, bool acknowledged)
    {
        var backend = new SimulatedGammaBackend();
        var store = new GammaCalibrationStore(_root);
        var client = new GammaPreviewClient(_root, "unused");
        var id = Guid.NewGuid();
        var preview = new GammaCalibrationTransaction(backend, store).Begin(id,
            CalibrationPreset.Create("Completion", new() { RedGain = .9 }), backend.State.DisplayId, 30);
        string directory = store.DirectoryFor(id);
        using var worker = PreviewWorkerFiles.TryLock(directory, "worker");
        Assert.NotNull(worker);
        var confirming = client.CommandAsync(id, "confirm");
        var command = ProfilePreviewStore.Read<GammaPreviewCommand>(Path.Combine(directory, "command.json"));
        var outcome = committed ? preview with
        {
            Status = "confirmed", ReasonCode = "confirmed", BindingStatus = "pending", ProfileCommitStarted = true,
            CurveReadbackVerified = true, InstalledProfile = $"DCCWPlus-{id:N}.icc"
        } : preview;
        if (committed) store.Save(outcome);
        if (acknowledged)
        {
            var ack = new GammaPreviewAck(command.Id, "calibration_failed", "Simulated worker error.");
            AtomicFile.Write(Path.Combine(directory, "ack.json"), JsonSerializer.SerializeToUtf8Bytes(ack, ProfilePreviewStore.Json));
        }

        if (committed)
        {
            var confirmed = await confirming.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("confirmed", confirmed.Status);
            Assert.True(confirmed.NeedsBindingCompletion);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ProfilePreviewException>(() => confirming.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("calibration_failed", error.Code);
        }
        Assert.Equal(outcome.Status, store.Load(id).Status);
    }

    private static async Task<GammaCalibrationSession> WaitForPreviewAsync(GammaCalibrationStore store, Guid id, Task<int> worker)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!worker.IsCompleted && store.Find(id) is not { Status: "previewing" })
            await Task.Delay(25, deadline.Token);
        if (worker.IsCompleted)
        {
            string[] errors = Directory.GetFiles(store.DirectoryFor(id), "*error.json");
            string details = string.Join(Environment.NewLine, errors.Select(File.ReadAllText));
            Assert.Fail($"The simulated gamma worker exited before previewing (exit {await worker}). {details}");
        }
        return store.Load(id);
    }

    private static string WriteRequest(GammaCalibrationStore store, GammaPreviewRequest request)
    {
        string path = Path.Combine(store.DirectoryFor(request.Id), "request.json");
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(request, ProfilePreviewStore.Json), overwrite: false);
        return path;
    }

    private static Process StartDisposableOwner()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "[void][Console]::ReadLine()" })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not open the disposable owner process.");
    }

    private static void StopDisposableOwner(Process owner)
    {
        if (owner.HasExited) return;
        owner.StandardInput.Close();
        if (!owner.WaitForExit(5000))
        {
            owner.Kill();
            Assert.True(owner.WaitForExit(5000));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class SimulatedProfileBackend : IProfileAssociationBackend
    {
        public ProfileAssociationState State { get; set; } = new("0123456789abcdef", "simulated-profile-target",
            1, new string('A', 64), "original.icc", "current-user", true, false,
            [new("original.icc", new string('B', 64), true), new("candidate.icc", new string('C', 64), true)],
            PersistenceKey: "path:" + new string('D', 64));

        public bool Disconnected { get; set; }
        public Action? BeforeRead { get; set; }
        public Action? BeforeWrite { get; set; }
        public int Writes { get; private set; }
        public void ChangeLayout() => State = State with { TopologyFingerprint = new string('E', 64) };
        public ProfileAssociationState Read(string displayId)
        {
            BeforeRead?.Invoke();
            if (Disconnected) throw new ProfilePreviewException("display_disconnected", "Simulated disconnected display.");
            return State;
        }
        public bool TrySetDefault(ProfileAssociationState expected, string profile, string sha256, Func<bool>? canWrite = null)
        {
            BeforeWrite?.Invoke();
            if (canWrite?.Invoke() == false) return false;
            Writes++;
            State = State with { CurrentProfile = profile };
            return true;
        }
    }

    private sealed class ProfileWorkerClock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero) + Elapsed;
    }

    private sealed class SimulatedGammaBackend : IGammaCalibrationBackend
    {
        public GammaDeviceState State { get; private set; } = new("abcd1234abcd1234", "simulated", "simulated-target",
            new string('A', 64), true, false, GammaRamp.Identity());
        public int Writes { get; private set; }
        public Action? BeforeWrite { get; set; }

        public GammaDeviceState Read(string displayId) => State with { Ramp = State.Ramp.Copy() };
        public bool TryWrite(GammaDeviceState expected, GammaRamp ramp, Func<bool>? canWrite = null)
        {
            BeforeWrite?.Invoke();
            if (canWrite?.Invoke() == false) return false;
            Writes++;
            State = State with { Ramp = ramp.Copy() };
            return true;
        }
        public GammaProfileSelection Commit(GammaCalibrationSession session) => throw new InvalidOperationException("These tests only preview.");
        public bool RestoreProfile(GammaCalibrationSession session, GammaDeviceState expected, Action? beforeSelectionRestore = null) =>
            throw new InvalidOperationException("These tests only preview.");
    }
}
