using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DccwPlus.Core.Calibration;

namespace DccwPlus.Windows;

public sealed class WindowsDisplayService : IDisplayService
{
    public DisplayProbe Probe()
    {
        try
        {
            var probed = DisplayConfigurationQuery.TryQuery();
            if (probed is not null) return probed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
        }
        return new("unknown", EnumerateGdi("unknown"));
    }

    public IReadOnlyList<DisplayInfo> Enumerate() => Probe().Displays;

    private IReadOnlyList<DisplayInfo> EnumerateGdi(string colorState)
    {
        List<DisplayInfo> result = [];
        for (uint i = 0; ; i++)
        {
            var adapter = Device.Create();
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
            if ((adapter.StateFlags & 1) == 0 || (adapter.StateFlags & 8) != 0) continue;
            bool found = false;
            for (uint j = 0; ; j++)
            {
                var monitor = Device.Create();
                if (!EnumDisplayDevices(adapter.DeviceName, j, ref monitor, 1)) break;
                if ((monitor.StateFlags & 1) == 0) continue;
                string identity = $"gdi:{monitor.DeviceId}|{adapter.DeviceName}";
                string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
                result.Add(new(id, DisplayName(monitor.DeviceString, adapter.DeviceName), adapter.DeviceName, (adapter.StateFlags & 4) != 0, colorState));
                found = true;
            }
            if (!found)
            {
                string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("gdi:" + adapter.DeviceName)))[..16].ToLowerInvariant();
                result.Add(new(id, DisplayName(adapter.DeviceString, adapter.DeviceName), adapter.DeviceName, (adapter.StateFlags & 4) != 0, colorState));
            }
        }
        return result;
    }

    private static string DisplayName(string? preferred, string fallback)
    {
        string name = string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();
        return name.Length > 128 ? name[..128] : name;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref Device display, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Device
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        public static Device Create() => new() { Size = Marshal.SizeOf<Device>(), DeviceName = "", DeviceString = "", DeviceId = "", DeviceKey = "" };
    }
}
