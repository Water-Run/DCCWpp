using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace DccwPlus.App;

public sealed partial class DisplayIdentificationWindow : Window
{
    private const int ExtendedStyleIndex = -20;
    private const int NoActivate = 0x08000000;
    private readonly DispatcherQueueTimer _timer;

    internal DisplayIdentificationWindow(string title, string name, string details, string id)
    {
        InitializeComponent();
        Title = title;
        MonitorName.Text = name;
        MonitorDetails.Text = details;
        MonitorId.Text = id;

        var presenter = OverlappedPresenter.CreateForToolWindow();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(4);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Close();
        Closed += (_, _) => _timer.Stop();
    }

    internal bool ShowOn(string deviceName)
    {
        // Keep the transient marker out of the keyboard focus order even when clicked.
        nint handle = WindowNative.GetWindowHandle(this);
        int style = GetWindowLong(handle, ExtendedStyleIndex);
        int readError = Marshal.GetLastPInvokeError();
        if (style == 0 && readError != 0) throw new Win32Exception(readError);
        int previousStyle = SetWindowLong(handle, ExtendedStyleIndex, style | NoActivate);
        int writeError = Marshal.GetLastPInvokeError();
        if (previousStyle == 0 && writeError != 0) throw new Win32Exception(writeError);
        if (!DisplayWindowPlacement.MoveToDisplay(this, deviceName, new SizeInt32(360, 168))) return false;
        AppWindow.Show(activateWindow: false);
        if (!DisplayWindowPlacement.IsOnDisplay(this, deviceName)) return false;
        AppWindow.Changed += (_, change) =>
        {
            if ((change.DidPositionChange || change.DidSizeChange) && AppWindow.IsVisible &&
                !DisplayWindowPlacement.IsOnDisplay(this, deviceName)) Close();
        };
        _timer.Start();
        return true;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true, SetLastError = true)]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", ExactSpelling = true, SetLastError = true)]
    private static extern int SetWindowLong(nint window, int index, int value);
}
