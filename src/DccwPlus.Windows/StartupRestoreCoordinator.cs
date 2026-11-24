using DccwPlus.Core.Calibration;

namespace DccwPlus.Windows;

public sealed class StartupRestoreCoordinator
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SettleInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BusyRetryWindow = GammaCalibrationTransaction.WindowPreviewLimit + TimeSpan.FromMinutes(1);
    private readonly Func<Task<bool>> _isEnabled;
    private readonly Func<bool, string, Task<CalibrationRestoreReport>> _restore;
    private readonly Func<string?> _readConfiguration;
    private readonly TimeProvider _time;

    public StartupRestoreCoordinator(string root)
    {
        var startup = new StartupService();
        var bindings = new CalibrationBindingService(root);
        _isEnabled = async () => (await startup.GetAsync()).IsEnabled;
        _restore = (enabled, trigger) => Task.Run(() => bindings.RunStartup(enabled, trigger));
        _readConfiguration = new WindowsDisplayService().ReadActivePathStamp;
        _time = TimeProvider.System;
    }

    internal StartupRestoreCoordinator(Func<Task<bool>> isEnabled, Func<bool, string, Task<CalibrationRestoreReport>> restore,
        Func<string?> readConfiguration, TimeProvider time)
    {
        _isEnabled = isEnabled;
        _restore = restore;
        _readConfiguration = readConfiguration;
        _time = time;
    }

    public async Task RunAsync(Func<TimeSpan, Task<string?>> waitForChange)
    {
        string? lastConfiguration = _readConfiguration();
        PendingRestore? pending = Start("sign_in");
        while (true)
        {
            if (!await _isEnabled())
            {
                if (pending is not null) await _restore(false, pending.Trigger);
                return;
            }

            if (pending is not null && _time.GetElapsedTime(pending.ScheduledAt) >= pending.Delay)
            {
                var report = await _restore(true, pending.Trigger);
                pending = ScheduleRetry(pending, report);
            }

            TimeSpan wait = pending is null ? PollInterval : pending.Delay - _time.GetElapsedTime(pending.ScheduledAt);
            if (wait <= TimeSpan.Zero) continue;
            string? trigger = await waitForChange(wait);
            string? configuration = _readConfiguration();
            if (trigger is null && configuration is not null && configuration != lastConfiguration)
                trigger = "display_changed";
            lastConfiguration = configuration;

            if (pending is null && trigger is not null) pending = Start(trigger);
            else if (pending is not null && trigger == "resume") pending = pending with { Trigger = trigger };
        }
    }

    private PendingRestore Start(string trigger)
    {
        long now = _time.GetTimestamp();
        return new(trigger, now, now, TimeSpan.Zero, 0);
    }

    private PendingRestore? ScheduleRetry(PendingRestore pending, CalibrationRestoreReport report)
    {
        long now = _time.GetTimestamp();
        if (report.Reason == "preview_busy")
        {
            TimeSpan remaining = BusyRetryWindow - _time.GetElapsedTime(pending.StartedAt, now);
            if (remaining <= TimeSpan.Zero) return null;
            return pending with { ScheduledAt = now, Delay = remaining < PollInterval ? remaining : PollInterval };
        }

        bool settling = report.Reason == "recovery_required"
            || report.Bindings.Any(binding => IsSettlingReason(binding.ReasonCode))
            || report.Recoveries.Any(item => item is { Kind: "binding", NeedsRecovery: true } && IsSettlingReason(item.ReasonCode));
        return settling && pending.SettlingRetries < 2
            ? pending with { ScheduledAt = now, Delay = SettleInterval, SettlingRetries = pending.SettlingRetries + 1 }
            : null;
    }

    private static bool IsSettlingReason(string reason) => reason is "display_disconnected" or "display_state_changed"
        or "profile_device_unavailable" or "profile_scope_unavailable" or "profile_state_unavailable" or "profile_file_unavailable"
        or "curve_read_failed";

    private sealed record PendingRestore(string Trigger, long StartedAt, long ScheduledAt, TimeSpan Delay, int SettlingRetries);
}
