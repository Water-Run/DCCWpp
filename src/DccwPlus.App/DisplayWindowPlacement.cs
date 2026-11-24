using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace DccwPlus.App;

internal static class DisplayWindowPlacement
{
    private const uint MonitorDefaultToNearest = 2;

    public static void SizeInitialWindow(Window window, SizeInt32 preferredSizeInDips)
    {
        nint handle = WindowNative.GetWindowHandle(window);
        nint monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (TryGetMonitorInfo(monitor, out var info) && info.WorkRect.HasArea)
        {
            uint dpi = GetDpiForWindow(handle);
            double scale = dpi == 0 ? 1 : dpi / 96d;
            var preferredPixels = new SizeInt32((int)Math.Round(preferredSizeInDips.Width * scale),
                (int)Math.Round(preferredSizeInDips.Height * scale));
            CenterInWorkArea(window.AppWindow, info.WorkRect, preferredPixels);
        }
    }

    public static bool IsOnDisplay(Window window, string deviceName)
    {
        nint monitor = MonitorFromWindow(WindowNative.GetWindowHandle(window), 0);
        return Matches(monitor, deviceName);
    }

    public static bool MoveToDisplay(Window window, string deviceName)
    {
        NativeRect targetWorkArea = default;
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            if (!TryGetMonitorInfo(monitor, out var info) || !NameMatches(info, deviceName)) return true;
            targetWorkArea = info.WorkRect;
            return false;
        }, 0);
        if (!targetWorkArea.HasArea) return false;
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } presenter)
            presenter.Restore();
        CenterInWorkArea(window.AppWindow, targetWorkArea, window.AppWindow.Size);
        return IsOnDisplay(window, deviceName);
    }

    private static void CenterInWorkArea(AppWindow window, NativeRect workArea, SizeInt32 requested)
    {
        int width = Math.Clamp(requested.Width, 1, workArea.Width);
        int height = Math.Clamp(requested.Height, 1, workArea.Height);
        // GetMonitorInfo and this AppWindow overload both use virtual desktop coordinates.
        window.MoveAndResize(new RectInt32(workArea.Left + (workArea.Width - width) / 2,
            workArea.Top + (workArea.Height - height) / 2, width, height));
    }

    private static bool Matches(nint monitor, string name) => TryGetMonitorInfo(monitor, out var info) && NameMatches(info, name);

    private static bool NameMatches(MonitorInfo info, string name) =>
        string.Equals(info.DeviceName, name, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetMonitorInfo(nint monitor, out MonitorInfo info)
    {
        info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), DeviceName = "" };
        return monitor != 0 && GetMonitorInfo(monitor, ref info);
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
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool HasArea => Width > 0 && Height > 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint context);

    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint context);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
