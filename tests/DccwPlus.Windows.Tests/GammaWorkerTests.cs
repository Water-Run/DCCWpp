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
