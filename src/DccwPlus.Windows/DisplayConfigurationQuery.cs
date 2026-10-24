using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DccwPlus.Core.Calibration;

namespace DccwPlus.Windows;

// Read-only QueryDisplayConfig and color-profile lookup. This file has no Set* display imports.
internal static class DisplayConfigurationQuery
{
    private const uint ActiveVirtualFlags = 0x00000002 | 0x00000010;
    private const uint DatabaseCurrent = 0x00000004;
    private const int ErrorInsufficientBuffer = 122;

    public static DisplayProbe? TryQuery()
    {
        if (Marshal.SizeOf<PathInfo>() != 72 || Marshal.SizeOf<ModeInfo>() != 64) return null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(ActiveVirtualFlags, out uint pathCount, out uint modeCount) != 0) return null;
            if (pathCount == 0) return new(ReadTopology(), []);
            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[Math.Max(modeCount, 1)];
            int result = QueryDisplayConfig(ActiveVirtualFlags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == ErrorInsufficientBuffer) continue;
            if (result != 0) return null;
            var names = ReadGdiDevices();
            var displays = new List<DisplayInfo>();
            string topology = ReadTopology();
            foreach (var path in paths.Take((int)pathCount))
            {
                if ((path.Flags & 1) == 0) continue;
                var display = Describe(path, modes, names, topology);
                if (displays.All(existing => existing.Id != display.Id)) displays.Add(display);
            }
            return new(topology, displays);
        }
        return null;
    }

    private static DisplayInfo Describe(PathInfo path, ModeInfo[] modes, Dictionary<string, GdiDevice> names, string topology)
    {
        string? gdiName = ReadSourceName(path.SourceAdapterLow, path.SourceAdapterHigh, path.SourceId);
        names.TryGetValue(gdiName ?? "", out var gdi);
        var target = ReadTarget(path.TargetAdapterLow, path.TargetAdapterHigh, path.TargetId);
        string? monitorName = MatchMonitor(target.MonitorPath, gdi.Monitors ?? []);
        string targetKey = $"{path.TargetAdapterHigh:X8}:{path.TargetAdapterLow:X8}:{path.TargetId}";
        string material = string.IsNullOrWhiteSpace(target.MonitorPath) ? "target:" + targetKey : "path:" + target.MonitorPath;
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant();
        int modeIndex = unchecked((int)(path.SourceModeInfo >> 16));
        int? width = null, height = null, x = null, y = null;
        if (modeIndex >= 0 && modeIndex < modes.Length && modes[modeIndex].InfoType == 1)
        {
            width = checked((int)modes[modeIndex].Width);
            height = checked((int)modes[modeIndex].Height);
            x = modes[modeIndex].PositionX;
            y = modes[modeIndex].PositionY;
        }
        var color = ReadColor(path.TargetAdapterLow, path.TargetAdapterHigh, path.TargetId);
        var profile = ReadProfile(path.TargetAdapterLow, path.TargetAdapterHigh, path.SourceId);
        int cloneGroup = unchecked((int)(path.SourceModeInfo & 0xFFFF));
        string? friendly = string.Equals(target.FriendlyName, "Display", StringComparison.OrdinalIgnoreCase) ? null : target.FriendlyName;
        string name = new string(FirstText(friendly, monitorName, target.FriendlyName, gdiName, "Display").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "Display";
        if (name.Length > 128) name = name[..128];
        bool primary = names.Values.Any(device => device.Primary) ? gdi.Primary : x == 0 && y == 0;
        int? reportedWidth = width is > 0 and <= 16384 ? width : null;
        int? reportedHeight = height is > 0 and <= 16384 ? height : null;
        string? profileName = SafeProfile(profile.Name);
        return new(id, name, string.IsNullOrWhiteSpace(gdiName) ? "DISPLAY" : gdiName, primary, color.State, "topology-query", topology, targetKey,
            OutputName(path.OutputTechnology), reportedWidth, reportedHeight, x, y,
            Millihertz(path.RefreshNumerator, path.RefreshDenominator), cloneGroup == 0xFFFF ? null : cloneGroup,
            profileName, profileName is null ? null : profile.Scope, color.HdrActive, color.AdvancedColorActive);
    }

    private static string ReadTopology()
    {
        if (GetDisplayConfigBufferSizes(DatabaseCurrent, out uint paths, out uint modes) != 0) return "unknown";
        var pathArray = new PathInfo[Math.Max(paths, 1)];
        var modeArray = new ModeInfo[Math.Max(modes, 1)];
        if (QueryDisplayConfig(DatabaseCurrent, ref paths, pathArray, ref modes, modeArray, out int topology) != 0) return "unknown";
        return topology switch { 1 => "internal", 2 => "clone", 4 => "extend", 8 => "external", _ => "unknown" };
    }

    private static string? ReadSourceName(uint low, int high, uint sourceId)
    {
        var request = new SourceName { Header = Header(1, Marshal.SizeOf<SourceName>(), low, high, sourceId) };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? Clean(request.Name) : null;
    }

    private static TargetDetails ReadTarget(uint low, int high, uint targetId)
    {
        var request = new NativeTarget { Header = Header(2, Marshal.SizeOf<NativeTarget>(), low, high, targetId) };
        if (DisplayConfigGetDeviceInfo(ref request) != 0) return new(null, null);
        return new(Clean(request.FriendlyName), Clean(request.MonitorPath));
    }

    private static ColorProbe ReadColor(uint low, int high, uint targetId)
    {
        var detailed = new AdvancedColor2 { Header = Header(15, Marshal.SizeOf<AdvancedColor2>(), low, high, targetId) };
        if (DisplayConfigGetDeviceInfo(ref detailed) == 0)
        {
            bool supported = Bit(detailed.Value, 0), active = Bit(detailed.Value, 1);
            string state = detailed.ActiveColorMode switch { 0 => "sdr", 1 => "wcg", 2 => "hdr", _ => active ? "advanced-color" : "unknown" };
            return new(state, detailed.ActiveColorMode == 2 ? true : false, active ? true : supported ? false : null);
        }
        var basic = new AdvancedColor { Header = Header(9, Marshal.SizeOf<AdvancedColor>(), low, high, targetId) };
        if (DisplayConfigGetDeviceInfo(ref basic) != 0) return new("unknown", null, null);
        bool enabled = Bit(basic.Value, 1);
        return new(enabled ? "advanced-color" : "sdr", enabled ? null : false, enabled ? true : false);
    }

    private static ProfileProbe ReadProfile(uint low, int high, uint sourceId)
    {
        var adapter = new Luid { LowPart = low, HighPart = high };
        int scope = 1;
        if (ColorProfileGetDisplayUserScope(adapter, sourceId, out int reported) >= 0 && reported is 0 or 1) scope = reported;
        string? name = ProfileName(adapter, sourceId, scope);
        if (name is null && scope != 1) name = ProfileName(adapter, sourceId, scope = 1);
        if (name is null && scope != 0) name = ProfileName(adapter, sourceId, scope = 0);
        return new(name, name is null ? null : scope == 0 ? "system-wide" : "current-user");
    }

    private static string? ProfileName(Luid adapter, uint sourceId, int scope)
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            if (ColorProfileGetDisplayDefault(scope, adapter, sourceId, 0, 4, out pointer) < 0 || pointer == IntPtr.Zero) return null;
            string? value = Clean(Marshal.PtrToStringUni(pointer));
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        finally { if (pointer != IntPtr.Zero) LocalFree(pointer); }
    }

    private static Dictionary<string, GdiDevice> ReadGdiDevices()
    {
        var result = new Dictionary<string, GdiDevice>(StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; ; i++)
        {
            var adapter = Device.Create();
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
            if ((adapter.StateFlags & 1) == 0 || (adapter.StateFlags & 8) != 0) continue;
            var monitors = new List<GdiMonitor>();
            for (uint monitorIndex = 0; ; monitorIndex++)
            {
                var monitor = Device.Create();
                if (!EnumDisplayDevices(adapter.DeviceName, monitorIndex, ref monitor, 1)) break;
                if ((monitor.StateFlags & 1) == 0) continue;
                string? monitorName = Clean(monitor.DeviceString);
                if (monitorName is null || string.Equals(monitorName, Clean(adapter.DeviceString), StringComparison.OrdinalIgnoreCase)) continue;
                monitors.Add(new(monitor.DeviceId, monitorName));
            }
            result[adapter.DeviceName] = new((adapter.StateFlags & 4) != 0, monitors);
        }
        return result;
    }

    private static string? MatchMonitor(string? monitorPath, IReadOnlyList<GdiMonitor> monitors)
    {
        if (monitors is null || monitors.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(monitorPath))
        {
            var matches = monitors.Where(monitor => string.Equals(monitor.DeviceId.Trim(), monitorPath.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) return matches[0].Name;
        }
        return monitors.Count == 1 ? monitors[0].Name : null;
    }

    private static DeviceInfo Header(int type, int size, uint low, int high, uint id) => new()
    { Type = type, Size = (uint)size, AdapterLow = low, AdapterHigh = high, Id = id };

    private static int? Millihertz(uint numerator, uint denominator) => numerator == 0 || denominator == 0
        ? null
        : (int)Math.Clamp(numerator * 1000L / denominator, 1, 1_000_000);

    private static string? SafeProfile(string? name)
    {
        name = Path.GetFileName(Clean(name));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." || name.Any(char.IsControl)) return null;
        return name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 ? null : name;
    }

    private static string OutputName(int value) => value switch
    {
        -1 => "other",
        0 => "hd15",
        1 => "svideo",
        2 => "composite",
        3 => "component",
        4 => "dvi",
        5 => "hdmi",
        6 => "lvds",
        8 => "d-jpn",
        9 => "sdi",
        10 => "displayport",
        11 => "displayport-embedded",
        12 => "udi",
        13 => "udi-embedded",
        14 => "sdtv-dongle",
        15 => "miracast",
        16 => "indirect-wired",
        17 => "indirect-virtual",
        18 => "displayport-usb-tunnel",
        unchecked((int)0x80000000) => "internal",
        _ => "other"
    };

    private static bool Bit(uint value, int shift) => (value & (1u << shift)) != 0;
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('\0');
    private static string FirstText(params string?[] values) => values.Select(Clean).FirstOrDefault(value => value is not null) ?? "Display";

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, out int topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceName request);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref NativeTarget request);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColor request);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColor2 request);
    [DllImport("mscms.dll")] private static extern int ColorProfileGetDisplayUserScope(Luid adapter, uint sourceId, out int scope);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern int ColorProfileGetDisplayDefault(int scope, Luid adapter, uint sourceId, int profileType, int profileSubtype, out IntPtr profileName);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref Device display, uint flags);

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public int Type; public uint Size; public uint AdapterLow; public int AdapterHigh; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SourceName
    {
        public DeviceInfo Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NativeTarget
    {
        public DeviceInfo Header;
        public uint Flags;
        public int OutputTechnology;
        public ushort EdidManufacturer;
        public ushort EdidProduct;
        public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorPath;
    }
    [StructLayout(LayoutKind.Sequential)] private struct AdvancedColor { public DeviceInfo Header; public uint Value; public uint Encoding; public uint BitsPerChannel; }
    [StructLayout(LayoutKind.Sequential)] private struct AdvancedColor2 { public DeviceInfo Header; public uint Value; public uint Encoding; public uint BitsPerChannel; public uint ActiveColorMode; }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo
    {
        public uint SourceAdapterLow; public int SourceAdapterHigh; public uint SourceId; public uint SourceModeInfo; public uint SourceStatus;
        public uint TargetAdapterLow; public int TargetAdapterHigh; public uint TargetId; public uint TargetModeInfo; public int OutputTechnology;
        public int Rotation; public int Scaling; public uint RefreshNumerator; public uint RefreshDenominator; public int ScanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable; public uint TargetStatus; public uint Flags;
    }
    [StructLayout(LayoutKind.Explicit, Size = 64)] private struct ModeInfo
    {
        [FieldOffset(0)] public int InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public uint AdapterLow;
        [FieldOffset(12)] public int AdapterHigh;
        [FieldOffset(16)] public uint Width;
        [FieldOffset(20)] public uint Height;
        [FieldOffset(24)] public int PixelFormat;
        [FieldOffset(28)] public int PositionX;
        [FieldOffset(32)] public int PositionY;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Device
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        public static Device Create() => new() { Size = Marshal.SizeOf<Device>(), DeviceName = "", DeviceString = "", DeviceId = "", DeviceKey = "" };
    }

    private readonly record struct GdiMonitor(string DeviceId, string Name);
    private readonly record struct GdiDevice(bool Primary, IReadOnlyList<GdiMonitor> Monitors);
    private readonly record struct TargetDetails(string? FriendlyName, string? MonitorPath);
    private readonly record struct ColorProbe(string State, bool? HdrActive, bool? AdvancedColorActive);
    private readonly record struct ProfileProbe(string? Name, string? Scope);
}

public sealed record DisplayProbe(string Topology, IReadOnlyList<DisplayInfo> Displays);
