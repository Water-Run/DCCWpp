using System.Diagnostics;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Storage;

namespace DccwPlus.Windows;

public sealed class PreviewSessionObserver(string root)
{
    public const int MaximumWaitSeconds = 60;

    public Task<(GammaCalibrationSession Session, string Outcome)> ObserveCalibrationAsync(Guid id, int waitSeconds)
    {
        var store = new GammaCalibrationStore(root);
        return ObserveAsync(store.DirectoryFor(id), () => store.Load(id),
            session => session.NeedsRecovery || session.NeedsBindingCompletion, waitSeconds);
    }

    public Task<(ProfilePreviewSession Session, string Outcome)> ObserveProfileAsync(Guid id, int waitSeconds)
    {
        var store = new ProfilePreviewStore(root);
        return ObserveAsync(store.DirectoryFor(id), () => store.Load(id), session => session.NeedsRecovery, waitSeconds);
    }

    private static async Task<(T Session, string Outcome)> ObserveAsync<T>(string directory, Func<T> load,
        Func<T, bool> hasPendingWork, int waitSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(waitSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(waitSeconds, MaximumWaitSeconds);
        var timeout = TimeSpan.FromSeconds(waitSeconds);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var (session, workerRunning) = PreviewWorkerFiles.Observe(directory, load, hasPendingWork);
            if (!hasPendingWork(session)) return (session, "finished");
            if (!workerRunning) return (session, "recovery_required");

            if (waitSeconds == 0) return (session, "in_progress");
            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) return (session, "wait_timeout");
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(150, remaining.TotalMilliseconds)));
        }
    }
}
