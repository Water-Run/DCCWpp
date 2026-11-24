using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;

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
    public string? ReadActivePathStamp()
    {
        string? stamp = DisplayConfigurationQuery.ReadActivePathStamp();
        return stamp is null ? null : (WindowsDisplaySession.IsRemote() ? "remote:" : "local:") + stamp;
    }

    internal static string? ComputeTopologyHash(string topology, IReadOnlyList<DisplayInfo> displays)
    {
        if (topology == "unknown" || displays.Count == 0 || displays.Any(display => display.TargetKey is null
            || display.SourceId is null || display.Width is null || display.Height is null || display.PositionX is null || display.PositionY is null))
            return null;

        // Sample the full desktop independently of the selected monitor and its ICC association.
        var paths = displays.OrderBy(display => display.Id, StringComparer.Ordinal).Select(display => new
        {
            display.Id, display.DeviceName, display.TargetKey, display.SourceId, display.PersistenceKey, display.IsPrimary,
            display.Output, display.Width, display.Height, display.PositionX, display.PositionY, display.RefreshMillihertz, display.CloneGroup,
            display.ColorState, display.HdrActive, display.AdvancedColorActive
        });
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { topology, paths })));
    }

    internal static string? HashProfileDeviceKey(string? key) =>
        key?.StartsWith(@"\Registry\Machine\System\", StringComparison.OrdinalIgnoreCase) == true
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant()))) : null;

    internal static DisplayInfo ResolveRecoveryDisplay(IReadOnlyList<DisplayInfo> connected, string originalId,
        string? persistenceKey, string? profileDeviceSha256)
    {
        if (!MonitorIdentity.IsPersistenceKey(persistenceKey) || !ProfilePreviewPolicy.IsHash(profileDeviceSha256))
            return connected.SingleOrDefault(display => display.Id == originalId)
                ?? throw new ProfilePreviewException("display_disconnected", new LocalizationService()["DisplayUnavailable"]);

        var matches = connected.Where(display => MonitorIdentity.SamePersistentIdentity(persistenceKey, display.PersistenceKey)).ToArray();
        if (matches.Length == 0)
            throw new ProfilePreviewException("display_disconnected", new LocalizationService()["DisplayUnavailable"]);
        if (matches.Length != 1)
            throw new ProfilePreviewException("display_identity_ambiguous", new LocalizationService()["DisplayUnavailable"]);
        var target = matches[0];
        if (!string.Equals(HashProfileDeviceKey(target.ProfileDeviceKey), profileDeviceSha256, StringComparison.OrdinalIgnoreCase)
            || connected.Count(display => string.Equals(display.ProfileDeviceKey, target.ProfileDeviceKey, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new ProfilePreviewException("profile_device_unavailable", new LocalizationService()["ProfileDeviceUnavailable"]);
        return target;
    }

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
