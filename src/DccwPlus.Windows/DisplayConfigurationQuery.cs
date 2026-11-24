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
    private const uint PathActive = 0x00000001;
    private const uint PathSupportsVirtualMode = 0x00000008;
    private const int SourceModeType = 1;
    private const int ErrorInsufficientBuffer = 122;
    private const int DefaultDisplayProfile = 4; // CPST_NONE selects the standard display profile.
    private const int ExtendedDisplayProfile = 8; // CPST_EXTENDED_DISPLAY_COLOR_MODE

    public static string? ReadActivePathStamp()
    {
        var configuration = ReadActiveConfiguration();
        if (configuration is null) return null;
        var stamps = configuration.Paths.Where(path => (path.Flags & PathActive) != 0).Select(path =>
        {
            var target = ReadTarget(path.TargetAdapterLow, path.TargetAdapterHigh, path.TargetId);
            var color = ReadColor(path.TargetAdapterLow, path.TargetAdapterHigh, path.TargetId);
            return PathStamp(path, configuration.Modes, target.MonitorPath, color);
        });
        return string.Join('\n', stamps.Order(StringComparer.Ordinal));
    }

    public static DisplayProbe? TryQuery()
    {
        var configuration = ReadActiveConfiguration();
        if (configuration is null) return null;
        string topology = ReadTopology();
        if (configuration.Paths.Length == 0) return new(topology, []);

        var names = ReadGdiDevices();
        var displays = new List<DisplayInfo>();
        foreach (var path in configuration.Paths)
        {
            if ((path.Flags & PathActive) == 0) continue;
            var display = Describe(path, configuration.Modes, names, topology);
            if (displays.All(existing => existing.Id != display.Id)) displays.Add(display);
        }
        return new(topology, displays);
    }

    private static ActiveConfiguration? ReadActiveConfiguration()
    {
        if (Marshal.SizeOf<PathInfo>() != 72 || Marshal.SizeOf<ModeInfo>() != 64) return null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(ActiveVirtualFlags, out uint pathCount, out uint modeCount) != 0) return null;
            if (pathCount == 0) return new([], []);
            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[Math.Max(modeCount, 1)];
            int result = QueryDisplayConfig(ActiveVirtualFlags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == ErrorInsufficientBuffer) continue;
            if (result != 0) return null;
            Array.Resize(ref paths, (int)pathCount);
            Array.Resize(ref modes, (int)modeCount);
            return new(paths, modes);
        }
        return null;
    }

    internal static ModeInfo? SourceMode(PathInfo path, ReadOnlySpan<ModeInfo> modes)
    {
        bool virtualPath = (path.Flags & PathSupportsVirtualMode) != 0;
        uint index = virtualPath ? path.SourceModeInfo >> 16 : path.SourceModeInfo;
        if ((virtualPath && index == ushort.MaxValue) || index >= (uint)modes.Length) return null;

        var mode = modes[(int)index];
        return mode.InfoType == SourceModeType && mode.Id == path.SourceId
            && mode.AdapterLow == path.SourceAdapterLow && mode.AdapterHigh == path.SourceAdapterHigh ? mode : null;
    }

    internal static int? CloneGroup(PathInfo path)
    {
        int group = (int)(path.SourceModeInfo & 0xffff);
        return (path.Flags & PathSupportsVirtualMode) != 0 && group != ushort.MaxValue ? group : null;
    }

    internal static string PathStamp(PathInfo path, ReadOnlySpan<ModeInfo> modes, string? monitorPath, ColorProbe color)
    {
        var mode = SourceMode(path, modes);
        string source = FormattableString.Invariant($"{path.SourceAdapterHigh:X8}{path.SourceAdapterLow:X8}:{path.SourceId}");
        string target = FormattableString.Invariant($"{path.TargetAdapterHigh:X8}{path.TargetAdapterLow:X8}:{path.TargetId}");
        string layout = FormattableString.Invariant($"{CloneGroup(path)}|{mode?.Width}|{mode?.Height}|{mode?.PositionX}|{mode?.PositionY}|{mode?.PixelFormat}");
        string signal = FormattableString.Invariant($"{path.Rotation}|{path.Scaling}|{Millihertz(path.RefreshNumerator, path.RefreshDenominator)}");
        return $"{source}|{target}|{monitorPath}|{path.TargetAvailable}|{layout}|{signal}|{color.State}";
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
        var mode = SourceMode(path, modes);
        int? x = mode?.PositionX, y = mode?.PositionY;
        var color = ReadColor(path.TargetAdapterLow, path.TargetAdapterHigh, path.TargetId);
        var profile = color.ProfileSubtype is int subtype
            ? WindowsColorProfileQuery.TryReadEffective(new() { Low = path.TargetAdapterLow, High = path.TargetAdapterHigh }, path.SourceId, subtype)
            : default;
        string? friendly = string.Equals(target.FriendlyName, "Display", StringComparison.OrdinalIgnoreCase) ? null : target.FriendlyName;
        string name = new string(FirstText(friendly, monitorName, target.FriendlyName, gdiName, "Display").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "Display";
        if (name.Length > 128) name = name[..128];
        bool primary = names.Values.Any(device => device.Primary) ? gdi.Primary : x == 0 && y == 0;
        int? reportedWidth = mode?.Width is > 0 and <= 16384 ? (int)mode.Value.Width : null;
        int? reportedHeight = mode?.Height is > 0 and <= 16384 ? (int)mode.Value.Height : null;
        string? profileName = profile.Name;
        var persistence = WindowsMonitorIdentity.Read(target.MonitorPath);
        return new(id, name, string.IsNullOrWhiteSpace(gdiName) ? "DISPLAY" : gdiName, primary, color.State, "topology-query", topology, targetKey,
            OutputName(path.OutputTechnology), reportedWidth, reportedHeight, x, y,
            Millihertz(path.RefreshNumerator, path.RefreshDenominator), CloneGroup(path),
            profileName, profileName is null ? null : profile.Scope, color.HdrActive, color.AdvancedColorActive,
            InstalledProfileReader.Read(profileName), path.SourceId,
            MatchMonitorKey(target.MonitorPath, gdi.Monitors ?? []), persistence?.Key, persistence?.Kind,
            profile.SelectedScope, profile.Status);
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
            return DescribeColor(detailed.ActiveColorMode, advancedColorActive: Bit(detailed.Value, 1));
        var basic = new AdvancedColor { Header = Header(9, Marshal.SizeOf<AdvancedColor>(), low, high, targetId) };
        if (DisplayConfigGetDeviceInfo(ref basic) != 0) return new("unknown", null, null);
        bool enabled = Bit(basic.Value, 1);
        return new(enabled ? "advanced-color" : "sdr", enabled ? null : false, enabled ? true : false);
    }

    internal static ColorProbe DescribeColor(uint mode, bool advancedColorActive) => mode switch
    {
        0 when !advancedColorActive => new("sdr", false, false),
        1 => new("wcg", false, true),
        2 => new("hdr", true, true),
        _ => advancedColorActive ? new("advanced-color", null, true) : new("unknown", null, null)
    };

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
                monitors.Add(new(monitor.DeviceId, monitorName, monitor.DeviceKey));
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

    private static string? MatchMonitorKey(string? path, IReadOnlyList<GdiMonitor> monitors)
    {
        var matches = monitors.Where(m => string.Equals(m.DeviceId, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        string? key = matches.Length == 1 ? matches[0].DeviceKey : monitors.Count == 1 ? monitors[0].DeviceKey : null;
        return key?.StartsWith(@"\Registry\Machine\System\", StringComparison.OrdinalIgnoreCase) == true ? key : null;
    }

    private static DeviceInfo Header(int type, int size, uint low, int high, uint id) => new()
    { Type = type, Size = (uint)size, AdapterLow = low, AdapterHigh = high, Id = id };

    private static int? Millihertz(uint numerator, uint denominator) => numerator == 0 || denominator == 0
        ? null
        : (int)Math.Clamp(numerator * 1000L / denominator, 1, 1_000_000);

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
    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref Device display, uint flags);

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
    [StructLayout(LayoutKind.Sequential)] internal struct PathInfo
    {
        public uint SourceAdapterLow; public int SourceAdapterHigh; public uint SourceId; public uint SourceModeInfo; public uint SourceStatus;
        public uint TargetAdapterLow; public int TargetAdapterHigh; public uint TargetId; public uint TargetModeInfo; public int OutputTechnology;
        public int Rotation; public int Scaling; public uint RefreshNumerator; public uint RefreshDenominator; public int ScanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable; public uint TargetStatus; public uint Flags;
    }
    [StructLayout(LayoutKind.Explicit, Size = 64)] internal struct ModeInfo
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

    private readonly record struct GdiMonitor(string DeviceId, string Name, string DeviceKey);
    private sealed record ActiveConfiguration(PathInfo[] Paths, ModeInfo[] Modes);
    private readonly record struct GdiDevice(bool Primary, IReadOnlyList<GdiMonitor> Monitors);
    private readonly record struct TargetDetails(string? FriendlyName, string? MonitorPath);
    internal readonly record struct ColorProbe(string State, bool? HdrActive, bool? AdvancedColorActive)
    {
        public int? ProfileSubtype => State switch
        {
            "sdr" or "wcg" => DefaultDisplayProfile,
            "hdr" => ExtendedDisplayProfile,
            _ => null
        };
    }
}

public sealed record DisplayProbe(string Topology, IReadOnlyList<DisplayInfo> Displays);
