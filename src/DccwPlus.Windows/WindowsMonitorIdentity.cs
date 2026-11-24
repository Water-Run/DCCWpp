using DccwPlus.Core.Calibration;
using Microsoft.Win32;

namespace DccwPlus.Windows;

internal static class WindowsMonitorIdentity
{
    public static MonitorPersistenceIdentity? Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        byte[] edid = [];
        string[] parts = path.Split('#');
        if (parts.Length >= 3 && parts[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)
            && Segment(parts[1]) && Segment(parts[2]))
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[1] + "\\" + parts[2] + @"\Device Parameters");
                edid = key?.GetValue("EDID") as byte[] ?? [];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return MonitorIdentity.FromEdid(edid, path);
    }
    private static bool Segment(string value) => value.Length is > 0 and <= 256
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '&' or '_' or '-');
}
