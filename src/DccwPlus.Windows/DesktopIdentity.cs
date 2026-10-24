using System.Runtime.InteropServices;

namespace DccwPlus.Windows;

public static class DesktopIdentity
{
    public static void Initialize()
    {
        if (!StartupService.HasPackageIdentity())
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID("DCCWPlus.Development.App"));
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
