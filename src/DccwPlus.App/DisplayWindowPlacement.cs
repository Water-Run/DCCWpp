using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace DccwPlus.App;

internal static class DisplayWindowPlacement
{
    public static bool IsOnDisplay(Window window, string deviceName)
    {
        nint monitor = MonitorFromWindow(WindowNative.GetWindowHandle(window), 0);
        return Matches(monitor, deviceName);
    }

    public static bool MoveToDisplay(Window window, string deviceName)
    {
        nint target = 0;
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            if (!Matches(monitor, deviceName)) return true;
            target = monitor;
            return false;
        }, 0);
        if (target == 0) return false;
        var area = DisplayArea.GetFromDisplayId(Win32Interop.GetDisplayIdFromMonitor(target));
        if (area is null || area.WorkArea.Width <= 0 || area.WorkArea.Height <= 0) return false;
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } presenter)
            presenter.Restore();
        var areaRect = area.WorkArea;
        var size = window.AppWindow.Size;
        int width = Math.Clamp(size.Width, 1, areaRect.Width);
        int height = Math.Clamp(size.Height, 1, areaRect.Height);
        window.AppWindow.MoveAndResize(new RectInt32(areaRect.X + (areaRect.Width - width) / 2,
            areaRect.Y + (areaRect.Height - height) / 2, width, height));
        return IsOnDisplay(window, deviceName);
    }

    private static bool Matches(nint monitor, string name)
    {
        if (monitor == 0) return false;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), DeviceName = "" };
        return GetMonitorInfo(monitor, ref info) && string.Equals(info.DeviceName, name, StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorRect, WorkRect;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint context);

    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint context);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
