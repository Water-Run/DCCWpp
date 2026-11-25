using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;
using static DccwPlus.Windows.WindowsColorProfileQuery;
using Luid = DccwPlus.Windows.WindowsColorProfileQuery.Luid;

namespace DccwPlus.Windows;

// Deliberately separate from the read-only display probe. Writes are owned by the preview and recovery workflows.
public sealed class WindowsProfileAssociationBackend : IProfileAssociationBackend
{
    public ProfileAssociationState Read(string displayId)
    {
        var displays = new WindowsDisplayService().Enumerate();
        var display = displays.SingleOrDefault(d => d.Id == displayId)
            ?? throw new ProfilePreviewException("display_disconnected", "The display is no longer connected.");
        return ReadAssociation(display, displays);
    }

    public ProfileAssociationState ReadRecoveryTarget(ProfileAssociationState original)
    {
        WindowsDisplaySession.RequireLocal();
        var displays = new WindowsDisplayService().Enumerate();
        var display = WindowsDisplayService.ResolveRecoveryDisplay(displays, original.DisplayId, original.PersistenceKey, original.ProfileDeviceSha256);
        return ReadAssociation(display, displays);
    }

    private static ProfileAssociationState ReadAssociation(DisplayInfo display, IReadOnlyList<DisplayInfo> displays)
    {
        if (display.TargetKey is null || display.SourceId is null)
            throw new ProfilePreviewException("display_identity_unavailable", "Windows did not report a display source identity.");
        var adapter = Adapter(display.TargetKey);
        int scope = ReadScope(adapter, display.SourceId.Value);
        // A profile preview requires a rollback destination in the same association scope.
        string? current = ReadDefault(adapter, display.SourceId.Value, scope);
        var profiles = List(adapter, display.SourceId.Value, scope).Select(Inspect).ToArray();
        return DescribeAssociation(display, displays, current, scope, profiles, WindowsDisplaySession.IsRemote());
    }

    internal static ProfileAssociationState DescribeAssociation(DisplayInfo display, IReadOnlyList<DisplayInfo> displays,
        string? current, int scope, IReadOnlyList<AssociatedProfile> profiles, bool isRemote)
    {
        // Older ICC journals only contain this fingerprint.
        string topology = string.Join('\n', displays.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d =>
            FormattableString.Invariant($"{d.Id}|{d.TargetKey}|{d.SourceId}|{d.Topology}|{d.ColorState}|{d.HdrActive}|{d.AdvancedColorActive}|{d.Width}|{d.Height}|{d.PositionX}|{d.PositionY}")));
        string? persistence = display.PersistenceKey;
        if (persistence is not null && displays.Count(other => other.PersistenceKey == persistence) != 1) persistence = null;
        return new(display.Id, display.TargetKey!, display.SourceId!.Value,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(topology))), current,
            scope == CurrentUser ? "current-user" : "system-wide", CalibrationService.IsExplicitSdr(display),
            display.Topology == "clone" || displays.Count(d => d.DeviceName == display.DeviceName) > 1, profiles, persistence,
            isRemote, ActiveTopologySha256: WindowsDisplayService.ComputeTopologyHash(display.Topology, displays),
            ProfileDeviceSha256: WindowsDisplayService.HashProfileDeviceKey(WindowsDisplayService.GetUniqueProfileDeviceKey(display, displays)));
    }

    public bool TrySetDefault(ProfileAssociationState expected, string profile, string sha256, Func<bool>? canWrite = null)
    {
        WindowsDisplaySession.RequireLocal();
        IccProfileInspector.ValidateFileName(profile);
        var now = Read(expected.DisplayId);
        if (!ProfilePreviewPolicy.MatchesProfileDevice(expected, now))
            throw new ProfilePreviewException("profile_device_unavailable", new LocalizationService()["ProfileDeviceUnavailable"]);
        if (!ProfilePreviewPolicy.SameTarget(expected, now)
            || !ProfilePreviewPolicy.EqualName(expected.CurrentProfile, now.CurrentProfile))
            throw new ProfilePreviewException("display_state_changed", "The display changed before the profile could be selected.");
        var current = ProfilePreviewPolicy.ValidProfile(expected, expected.CurrentProfile);
        if (current is null || ProfilePreviewPolicy.ValidProfile(now, now.CurrentProfile)?.Sha256 != current.Sha256
            || ProfilePreviewPolicy.ValidProfile(now, profile)?.Sha256 != sha256)
            throw new ProfilePreviewException("profile_file_changed", "The associated profile is missing, invalid, or has changed.");
        var adapter = Adapter(now.TargetKey);
        if (canWrite?.Invoke() == false) return false;
        Check(ColorProfileSetDisplayDefaultAssociation(CurrentUser, profile, Icc, StandardDisplay, adapter, now.SourceId), "profile_write_failed");
        return true;
    }

    private static AssociatedProfile Inspect(string fileName)
    {
        try { return InspectProfile(fileName, InstalledProfileReader.ReadBytes(fileName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { return new(fileName, null, false); }
    }

    internal static AssociatedProfile InspectProfile(string fileName, ReadOnlySpan<byte> bytes)
    {
        var report = IccProfileInspector.Inspect(fileName, bytes);
        bool valid = report.ReadStatus == "read" && report.SignatureValid && report.DeviceClass == "mntr"
            && report.ColorSpace == "RGB" && report.Mhc2Present is not null;
        return new(fileName, Convert.ToHexString(SHA256.HashData(bytes)), valid, report.Mhc2Present);
    }

    internal static IReadOnlyList<string> List(string targetKey, uint source, int scope) => List(Adapter(targetKey), source, scope);

    private static IReadOnlyList<string> List(Luid adapter, uint source, int scope)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int result = ColorProfileGetDisplayList(scope, adapter, source, out buffer, out uint count);
            if (IsAbsent(result)) return [];
            Check(result, "profile_list_unavailable");
            if (count > 256 || count > 0 && buffer == IntPtr.Zero) throw new InvalidDataException("Invalid profile list returned by Windows.");
            List<string> profiles = [];
            for (int index = 0; index < count; index++)
            {
                string? value = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, index * IntPtr.Size));
                if (string.IsNullOrWhiteSpace(value)) continue;
                value = Path.GetFileName(value);
                IccProfileInspector.ValidateFileName(value);
                if (!profiles.Contains(value, StringComparer.OrdinalIgnoreCase)) profiles.Add(value);
            }
            return profiles;
        }
        finally { if (buffer != IntPtr.Zero) LocalFree(buffer); }
    }

    private static void Check(int result, string code)
    { if (result < 0) throw new ProfilePreviewException(code, $"Windows profile operation failed (0x{result:X8})."); }
    [DllImport("mscms.dll")] private static extern int ColorProfileGetDisplayList(int scope, Luid adapter, uint source, out IntPtr list, out uint count);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern int ColorProfileSetDisplayDefaultAssociation(int scope, string name, int type, int subtype, Luid adapter, uint source);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
