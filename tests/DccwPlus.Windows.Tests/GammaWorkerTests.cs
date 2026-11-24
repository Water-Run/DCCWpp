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
            Assert.Equal("preview_busy", startup.RunStartup(true, "sign_in").Reason);
            Assert.Equal("sign_in", startup.LastStartupReport()?.Trigger);
            Assert.Equal("previewing", store.Load(id).Status);
            Assert.Equal(1, backend.Writes);
            int duplicate = await Task.Run(() => GammaPreviewHost.Run(_root, id,
                () => throw new InvalidOperationException("A second backend must not open a session."), attemptId: Guid.NewGuid()));
            Assert.Equal(10, duplicate);
            var a = new GammaPreviewClient(_root, "unused");
            var b = new GammaPreviewClient(_root, "unused");
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

            Assert.Equal("reverted", (await a.CommandAsync(id, "revert")).Status);
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
            Assert.Equal(store.Load(id).ExpiresAt, (await client.BeginAsync(displayId, "candidate.icc", requestId: id)).ExpiresAt);
            Assert.Equal("reverted", (await client.CommandAsync(id, "revert")).Status);
            Assert.Equal(0, await worker.WaitAsync(TimeSpan.FromSeconds(4)));
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
    public void RecoveryUsesTheSavedSessionAndKeepsTheRequest()
    {
        var store = new GammaCalibrationStore(_root);
        var preset = CalibrationPreset.Create("Original", new() { BlueGain = .85 });
        var backend = new SimulatedGammaBackend();
        Guid id = Guid.NewGuid();
        var request = new GammaPreviewRequest(id, backend.State.DisplayId, preset, 30, AllowRetries: true);
        string path = WriteRequest(store, request);
        byte[] original = File.ReadAllBytes(path);
        new GammaCalibrationTransaction(backend, store).Begin(id, preset, request.DisplayId, 30);

        Assert.Equal(0, GammaPreviewHost.Run(_root, id, () => backend, attemptId: Guid.NewGuid(), recover: true));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal("reverted", store.Load(id).Status);
        Assert.True(backend.State.Ramp.Matches(GammaRamp.Identity()));
    }

    private static async Task<GammaCalibrationSession> WaitForPreviewAsync(GammaCalibrationStore store, Guid id, Task<int> worker)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!worker.IsCompleted && store.Find(id) is not { Status: "previewing" })
            await Task.Delay(25, deadline.Token);
        Assert.False(worker.IsCompleted, "The fake worker exited before previewing.");
        return store.Load(id);
    }

    private static string WriteRequest(GammaCalibrationStore store, GammaPreviewRequest request)
    {
        string path = Path.Combine(store.DirectoryFor(request.Id), "request.json");
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(request, ProfilePreviewStore.Json), overwrite: false);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class SimulatedProfileBackend : IProfileAssociationBackend
    {
        public ProfileAssociationState State { get; private set; } = new("0123456789abcdef", "simulated-profile-target",
            1, new string('A', 64), "original.icc", "current-user", true, false,
            [new("original.icc", new string('B', 64), true), new("candidate.icc", new string('C', 64), true)],
            PersistenceKey: "path:" + new string('D', 64));

        public bool Disconnected { get; set; }
        public void ChangeLayout() => State = State with { TopologyFingerprint = new string('E', 64) };
        public ProfileAssociationState Read(string displayId) => Disconnected
            ? throw new ProfilePreviewException("display_disconnected", "Simulated disconnected display.")
            : State;
        public void SetDefault(ProfileAssociationState expected, string profile, string sha256) =>
            State = State with { CurrentProfile = profile };
    }

    private sealed class SimulatedGammaBackend : IGammaCalibrationBackend
    {
        public GammaDeviceState State { get; private set; } = new("abcd1234abcd1234", "simulated", "simulated-target",
            new string('A', 64), true, false, GammaRamp.Identity());
        public int Writes { get; private set; }

        public GammaDeviceState Read(string displayId) => State with { Ramp = State.Ramp.Copy() };
        public void Write(GammaDeviceState expected, GammaRamp ramp)
        {
            Writes++;
            State = State with { Ramp = ramp.Copy() };
        }
        public string Commit(GammaCalibrationSession session) => throw new InvalidOperationException("These tests only preview.");
        public void RestoreProfile(GammaCalibrationSession session) => throw new InvalidOperationException("These tests only preview.");
    }
}
