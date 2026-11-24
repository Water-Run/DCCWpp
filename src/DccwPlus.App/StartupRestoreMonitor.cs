using System.Runtime.InteropServices;
using System.Threading.Channels;
using DccwPlus.Core.Storage;
using DccwPlus.Windows;
using Microsoft.Win32;

namespace DccwPlus.App;

internal sealed class StartupRestoreMonitor
{
    private readonly Channel<bool> _events = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private int _resumeRequested;

    public async Task RunAsync()
    {
        using var lockFile = TryAcquireMonitor();
        if (lockFile is null) return;
        bool watchingDisplay = false, watchingPower = false;
        try
        {
            // Poll the active layout and color modes even when Windows does not offer system events.
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

            await new StartupRestoreCoordinator(DataPaths.DefaultRoot).RunAsync(WaitForChangeAsync);
        }
        finally
        {
            if (watchingDisplay) SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            if (watchingPower) SystemEvents.PowerModeChanged -= PowerChanged;
        }
    }

    private async Task<string?> WaitForChangeAsync(TimeSpan timeout)
    {
        using var interval = new CancellationTokenSource(timeout);
        try { await _events.Reader.ReadAsync(interval.Token); }
        catch (OperationCanceledException) when (interval.IsCancellationRequested) { return null; }
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        _events.Reader.TryRead(out _);
        return Interlocked.Exchange(ref _resumeRequested, 0) == 0 ? "display_changed" : "resume";
    }

    private void DisplayChanged(object? sender, EventArgs e) => _events.Writer.TryWrite(true);
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Interlocked.Exchange(ref _resumeRequested, 1);
        _events.Writer.TryWrite(true);
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
