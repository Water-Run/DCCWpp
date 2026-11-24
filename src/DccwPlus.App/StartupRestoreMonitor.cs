using System.Runtime.InteropServices;
using System.Threading.Channels;
using DccwPlus.Core.Storage;
using DccwPlus.Windows;
using Microsoft.Win32;

namespace DccwPlus.App;

internal sealed class StartupRestoreMonitor
{
    private readonly StartupService _startup = new();
    private readonly CalibrationBindingService _bindings = new(DataPaths.DefaultRoot);
    private readonly WindowsDisplayService _displays = new();
    private readonly Channel<string> _events = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    public async Task RunAsync()
    {
        using var lockFile = TryAcquireMonitor();
        if (lockFile is null) return;
        bool watchingDisplay = false, watchingPower = false;
        try
        {
            // Even when Windows does not offer system events, active path polling still covers reconnect.
            try
            {
                SystemEvents.DisplaySettingsChanged += DisplayChanged;
                watchingDisplay = true;
                SystemEvents.PowerModeChanged += PowerChanged;
                watchingPower = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }

            string? lastPaths = _displays.ReadActivePathStamp();
            if (!await RestoreAsync("sign_in")) return;
            while (true)
            {
                string? trigger = await WaitForChangeAsync();
                if (!(await _startup.GetAsync()).IsEnabled) return;
                string? paths = _displays.ReadActivePathStamp();
                if (trigger is null && paths is not null && paths != lastPaths) trigger = "display_changed";
                lastPaths = paths ?? lastPaths;
                if (trigger is not null && !await RestoreAsync(trigger)) return;
            }
        }
        finally
        {
            if (watchingDisplay) SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            if (watchingPower) SystemEvents.PowerModeChanged -= PowerChanged;
        }
    }

    private async Task<bool> RestoreAsync(string trigger)
    {
        for (int attempt = 0; ; attempt++)
        {
            bool enabled = (await _startup.GetAsync()).IsEnabled;
            var report = await Task.Run(() => _bindings.RunStartup(enabled, trigger));
            if (!enabled) return false;
            bool retryable = report.Reason is "preview_busy" or "recovery_required"
                || report.Bindings.Any(b => b.ReasonCode is "display_disconnected" or "display_state_changed");
            if (!retryable || attempt >= 2) return true;
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    private async Task<string?> WaitForChangeAsync()
    {
        using var interval = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string trigger;
        try { trigger = await _events.Reader.ReadAsync(interval.Token); }
        catch (OperationCanceledException) when (interval.IsCancellationRequested) { return null; }
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        while (_events.Reader.TryRead(out var later))
            if (later == "resume") trigger = later;
        return trigger;
    }

    private void DisplayChanged(object? sender, EventArgs e) => _events.Writer.TryWrite("display_changed");
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _events.Writer.TryWrite("resume");
    }

    private static FileStream? TryAcquireMonitor()
    {
        Directory.CreateDirectory(DataPaths.DefaultRoot);
        try
        {
            return new FileStream(Path.Combine(DataPaths.DefaultRoot, "startup-monitor.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return null; }
    }
}
