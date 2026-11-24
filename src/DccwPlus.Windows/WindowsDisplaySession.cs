using System.Runtime.InteropServices;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;

namespace DccwPlus.Windows;

public static class WindowsDisplaySession
{
    private const int RemoteSessionMetric = 0x1000;
    private const int CurrentSession = -1;
    private const int ClientProtocolType = 16;

    public static bool IsRemote()
    {
        if (GetSystemMetrics(RemoteSessionMetric) != 0) return true;

        // The protocol query also detects remote sessions that are exposed as a console by the graphics stack.
        if (!WTSQuerySessionInformationW(IntPtr.Zero, CurrentSession, ClientProtocolType, out var buffer, out int bytes))
            return false;
        try { return buffer != IntPtr.Zero && bytes >= sizeof(ushort) && Marshal.ReadInt16(buffer) != 0; }
        finally { if (buffer != IntPtr.Zero) WTSFreeMemory(buffer); }
    }

    internal static void RequireLocal()
    {
        if (IsRemote()) throw new ProfilePreviewException("remote_session_blocked", new LocalizationService()["RemoteCalibrationBlocked"]);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int informationClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr buffer);
}
