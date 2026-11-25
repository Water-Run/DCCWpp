using System.Runtime.InteropServices;
using DccwPlus.Core.Calibration;

namespace DccwPlus.Windows;

public sealed class StartupRestoreCoordinator
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SettleInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StartupQueryTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BusyRetryWindow = GammaCalibrationTransaction.WindowPreviewLimit + TimeSpan.FromMinutes(1);
    private readonly Func<Task<string?>> _checkPermission;
    private readonly Func<string, string?, Task<CalibrationRestoreReport>> _restore;
    private readonly Func<string?> _readConfiguration;
    private readonly TimeProvider _time;

    public StartupRestoreCoordinator(string root)
    {
        var startup = new StartupService();
        var bindings = new CalibrationBindingService(root);
        _checkPermission = () => CheckPermissionAsync(startup.GetAsync, StartupQueryTimeout);
        // The worker must keep the display mutex on one thread while rechecking Windows permission.
        _restore = (trigger, failure) => Task.Run(() =>
            bindings.RunStartup(trigger, () => _checkPermission().GetAwaiter().GetResult(), failure));
        _readConfiguration = new WindowsDisplayService().ReadActivePathStamp;
        _time = TimeProvider.System;
    }

    internal StartupRestoreCoordinator(Func<Task<string?>> checkPermission, Func<string, string?, Task<CalibrationRestoreReport>> restore,
        Func<string?> readConfiguration, TimeProvider time)
    {
        _checkPermission = checkPermission;
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
            string? permissionFailure = await _checkPermission();
            if (permissionFailure == "startup_disabled")
            {
                if (pending is not null) await _restore(pending.Trigger, permissionFailure);
                return;
            }

            if (pending is not null && _time.GetElapsedTime(pending.ScheduledAt) >= pending.Delay)
            {
                if (permissionFailure is not null)
                {
                    if (!pending.WaitingForPermission) await _restore(pending.Trigger, permissionFailure);
                    pending = pending with
                    {
                        ScheduledAt = _time.GetTimestamp(),
                        Delay = pending.WaitingForPermission ? PollInterval : SettleInterval,
                        WaitingForPermission = true
                    };
                }
                else
                {
                    if (pending.WaitingForPermission) pending = Start(pending.Trigger);
                    var report = await _restore(pending.Trigger, null);
                    if (report.Reason == "startup_disabled") return;
                    pending = ScheduleRetry(pending, report);
                }
            }

            TimeSpan wait = pending is null ? PollInterval : pending.Delay - _time.GetElapsedTime(pending.ScheduledAt);
            if (wait <= TimeSpan.Zero) continue;
            string? trigger = await waitForChange(wait);
            string? configuration = _readConfiguration();
            bool configurationChanged = configuration is not null && configuration != lastConfiguration;
            lastConfiguration = configuration;

            if (pending is null)
            {
                if (trigger is not null || configurationChanged) pending = Start(trigger ?? "display_changed");
            }
            else if (trigger == "resume" || configurationChanged)
            {
                // Give the new event a full retry budget while keeping the next attempt and permission backoff in place.
                string reason = trigger == "resume" || pending.Trigger == "resume" ? "resume" : "display_changed";
                pending = pending with { Trigger = reason, StartedAt = _time.GetTimestamp(), SettlingRetries = 0 };
            }
        }
    }

    private PendingRestore Start(string trigger)
    {
        long now = _time.GetTimestamp();
        return new(trigger, now, now, TimeSpan.Zero, 0, WaitingForPermission: false);
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

        bool settling = report.Reason is "recovery_required" or "startup_unavailable"
            || report.Bindings.Any(binding => IsSettlingReason(binding.ReasonCode))
            || report.Recoveries.Any(item => item is { Kind: "binding", NeedsRecovery: true } && IsSettlingReason(item.ReasonCode));
        return settling && pending.SettlingRetries < 2
            ? pending with { ScheduledAt = now, Delay = SettleInterval, SettlingRetries = pending.SettlingRetries + 1 }
            : null;
    }

    private static bool IsSettlingReason(string reason) => reason is "display_disconnected" or "display_state_changed"
        or "profile_device_unavailable" or "profile_scope_unavailable" or "profile_state_unavailable" or "profile_file_unavailable"
        or "curve_read_failed";

    internal static async Task<string?> CheckPermissionAsync(Func<CancellationToken, Task<StartupStatus>> readStatus, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            var status = await readStatus(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            return status.State switch
            {
                "Enabled" or "EnabledByPolicy" when status.IsEnabled => null,
                "Disabled" or "DisabledByUser" or "DisabledByPolicy" => "startup_disabled",
                _ => "startup_unavailable"
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            return "startup_unavailable";
        }
    }

    private sealed record PendingRestore(string Trigger, long StartedAt, long ScheduledAt, TimeSpan Delay, int SettlingRetries,
        bool WaitingForPermission);
}
