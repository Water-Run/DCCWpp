using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Storage;
using static DccwPlus.Windows.WindowsColorProfileQuery;
using Luid = DccwPlus.Windows.WindowsColorProfileQuery.Luid;

namespace DccwPlus.Windows;

public sealed record IccAssociationBackup(int Scope, string? UserDefault, uint SourceId, string TargetKey,
    string CandidateName, string DeviceKey, string? EffectiveDefault = null, string? EffectiveDefaultHash = null,
    string? UserDefaultHash = null, string? CandidateHash = null);

public sealed record SdrCalibrationDiagnostics(string DisplayId, string ColorState, string? ProfileName, string? ProfileScope,
    bool? WindowsCalibrationLoaderEnabled, bool? DriverSupportsVcgt, bool? LogicalRampReadable, string ReasonCode,
    bool? ProfileMhc2Present = null, string? SelectedProfileScope = null, string? ProfileQueryStatus = null);

// SDR compatibility backend. Never used for HDR/ACM, mirrored sources, or an unknown display.
// Readback verifies the driver's logical gamma table, not optical color accuracy.
public sealed partial class WindowsGammaCalibrationBackend(string root) : IGammaCalibrationBackend
{
    private readonly GammaCalibrationStore _store = new(root);
    public GammaDeviceState Read(string displayId)
    {
        var displays = new WindowsDisplayService().Enumerate();
        var display = displays.SingleOrDefault(d => d.Id == displayId)
            ?? throw new ProfilePreviewException("display_disconnected", "The display is not connected.");
        if (display.TargetKey is null || display.SourceId is null)
            throw new ProfilePreviewException("display_identity_unavailable", "The display source identity is unavailable.");
        if (display.ProfileQueryStatus == "unavailable")
            throw new ProfilePreviewException("profile_state_unavailable", "Windows could not read the display's current color profile or scope.");
        string material = string.Join('\n', displays.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d =>
            FormattableString.Invariant($"{d.Id}|{d.TargetKey}|{d.SourceId}|{d.DeviceName}|{d.Topology}|{d.ColorState}|{d.Width}|{d.Height}|{d.PositionX}|{d.PositionY}|{d.HdrActive}|{d.AdvancedColorActive}")));
        return new(display.Id, display.DeviceName, display.TargetKey,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))), CalibrationService.IsExplicitSdr(display),
            display.Topology == "clone" || displays.Count(d => d.DeviceName == display.DeviceName) > 1,
            ReadRamp(display.DeviceName), display.ProfileName, display.ProfileScope, display.SourceId, display.PersistenceKey,
            display.Profile?.Mhc2Present, display.SelectedProfileScope);
    }
    public SdrCalibrationDiagnostics Diagnose(string displayId)
    {
        var displays = new WindowsDisplayService().Enumerate();
        var display = displays.SingleOrDefault(d => d.Id == displayId)
            ?? throw new KeyNotFoundException("The requested display is not connected.");
        bool? loader = WcsGetCalibrationManagementState(out int enabled) ? enabled != 0 : null;
        bool? vcgt = null;
        bool? readable = null;
        string reason = "logical_ramp_readable";
        if (!CalibrationService.IsExplicitSdr(display))
            reason = "sdr_path_blocked";
        else if (display.Profile?.Mhc2Present == true)
            reason = "mhc2_profile_blocked";
        else if (display.ProfileQueryStatus == "unavailable")
            reason = "profile_state_unavailable";
        else if (display.TargetKey is null || display.SourceId is null)
            reason = "display_identity_unavailable";
        else if (display.Topology == "clone" || displays.Count(d => d.DeviceName == display.DeviceName) > 1)
            reason = "shared_source_blocked";
        else
        {
            var adapter = Adapter(display.TargetKey);
            uint source = display.SourceId.Value;
            if (display.SelectedProfileScope is "current-user" or "system-wide")
            {
                int scope = display.SelectedProfileScope == "current-user" ? CurrentUser : SystemWide;
                var caps = new VcgtCapabilities { Size = (uint)Marshal.SizeOf<VcgtCapabilities>() };
                try
                {
                    if (ColorProfileGetDeviceCapabilities(scope, adapter, source, 1, ref caps) >= 0) vcgt = caps.Supported != 0;
                }
                catch (EntryPointNotFoundException) { }
            }
            try
            {
                ReadRamp(display.DeviceName);
                readable = true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ProfilePreviewException)
            {
                readable = false;
                reason = "curve_read_failed";
            }
        }
        return new(display.Id, display.ColorState, display.ProfileName, display.ProfileScope, loader, vcgt, readable, reason,
            display.Profile?.Mhc2Present, display.SelectedProfileScope, display.ProfileQueryStatus);
    }
    public void Write(GammaDeviceState expected, GammaRamp ramp)
    {
        ramp.Validate();
        var now = Read(expected.DisplayId);
        if (now.ProfileMhc2Present == true)
            throw new ProfilePreviewException("mhc2_profile_blocked", "The selected color profile contains MHC2 hardware calibration.");
        if (!GammaCalibrationTransaction.SameTarget(expected, now) || !GammaCalibrationTransaction.SameProfile(expected, now)
            || !now.Ramp.Matches(expected.Ramp, 257))
            throw new ProfilePreviewException("display_state_changed", "The display changed before its calibration curve could be written.");
        using var dc = DisplayDc.Create(now.DeviceName);
        var native = NativeRamp.From(ramp);
        if (!SetDeviceGammaRamp(dc.Handle, ref native))
            throw new ProfilePreviewException("curve_write_failed", "The display driver rejected the calibration curve.");
    }
    public string? Commit(GammaCalibrationSession session)
    {
        if (session.UndoOf is Guid calibrationId) return CommitUndo(session, calibrationId);
        var device = new WindowsDisplayService().Enumerate().Single(d => d.Id == session.Original.DisplayId);
        var now = Read(device.Id);
        if (!GammaCalibrationTransaction.SameTarget(session.Original, now) || !GammaCalibrationTransaction.SameProfile(session.Original, now)
            || !now.Ramp.Matches(session.Candidate, 257))
            throw new ProfilePreviewException("display_state_changed", "The display changed before the calibration could be saved.");
        var adapter = Adapter(device.TargetKey!);
        uint source = device.SourceId!.Value;
        string deviceKey = device.ProfileDeviceKey ?? throw new ProfilePreviewException("profile_device_unavailable", "Windows did not report the profile device identity.");
        int scope = ReadScope(adapter, source);
        if (now.SelectedProfileScope != ScopeName(scope))
            throw new ProfilePreviewException("external_profile_change", "The selected color profile scope changed before saving.");
        string? userDefault = ReadDefault(adapter, source, 1);
        string? effective = (scope == 1 ? userDefault : null) ?? ReadDefault(adapter, source, 0);
        if (!ProfilePreviewPolicy.EqualName(effective, session.Original.ProfileName))
            throw new ProfilePreviewException("external_profile_change", "The original color profile changed before it could be saved.");
        string baseName = effective ?? "sRGB Color Space Profile.icm";
        byte[] original = InstalledProfileReader.ReadBytes(baseName);
        string candidate = "DCCWPlus-" + session.Id.ToString("N") + ".icc";
        string directory = _store.DirectoryFor(session.Id);
        byte[] profile = IccCalibrationWriter.Build(original, session.Candidate, session.Id);
        string? effectiveHash = effective is null ? null : Convert.ToHexString(SHA256.HashData(original));
        string? userHash = userDefault is null ? null
            : ProfilePreviewPolicy.EqualName(userDefault, effective) ? effectiveHash : HashInstalled(userDefault);
        var backup = new IccAssociationBackup(scope, userDefault, source, device.TargetKey!, candidate, deviceKey,
            effective, effectiveHash, userHash, Convert.ToHexString(SHA256.HashData(profile)));
        string backupPath = Path.Combine(directory, "association.json");
        AtomicFile.Write(backupPath, JsonSerializer.SerializeToUtf8Bytes(backup, ProfilePreviewStore.Json), overwrite: false);
        string path = Path.Combine(directory, candidate);
        AtomicFile.Write(path, profile, overwrite: false);
        if (!InstallColorProfile(null, path))
            throw new ProfilePreviewException("profile_install_failed", $"Windows could not install the calibration profile (Win32 {Marshal.GetLastWin32Error()}).");
        var beforeAssociation = Read(device.Id);
        int beforeScope = ReadScope(adapter, source);
        if (!GammaCalibrationTransaction.SameTarget(session.Original, beforeAssociation)
            || !GammaCalibrationTransaction.SameProfile(session.Original, beforeAssociation)
            || beforeScope != scope || !ProfilePreviewPolicy.EqualName(ReadDefault(adapter, source, 1), userDefault)
            || !beforeAssociation.Ramp.Matches(session.Candidate, 257))
            throw new ProfilePreviewException("display_state_changed", "The display changed while the calibration profile was being installed.");
        AtomicFile.Write(Path.Combine(directory, "association-mutation-started"), []);
        if (scope != 1) SetUserScope(deviceKey, true);
        Check(ColorProfileAddDisplayAssociation(1, candidate, adapter, source, true, false));
        var associated = Read(device.Id);
        if (!GammaCalibrationTransaction.SameTarget(session.Original, associated)
            || associated.ProfileScope != "current-user" || !ProfilePreviewPolicy.EqualName(associated.ProfileName, candidate))
            throw new ProfilePreviewException("association_not_verified", "Windows did not report the saved calibration profile as default.");
        // Profile notification may reload calibration. Reapply only this same confirmed curve.
        if (!associated.Ramp.Matches(session.Candidate, 257)) Write(associated, session.Candidate);
        return candidate;
    }
    public void RestoreProfile(GammaCalibrationSession session)
    {
        string directory = _store.DirectoryFor(session.Id);
        var current = Read(session.Original.DisplayId);
        if (!File.Exists(Path.Combine(directory, "association-mutation-started")))
        {
            if (!GammaCalibrationTransaction.SameProfile(session.Original, current))
                throw new ProfilePreviewException("external_profile_change", "Another application selected a different profile.");
            return;
        }
        if (session.UndoOf is Guid calibrationId)
        {
            RestoreUndoProfile(session, calibrationId, current);
            return;
        }
        RestoreSavedAssociation(session, ReadAssociationBackup(session), current);
    }

    private void RestoreSavedAssociation(GammaCalibrationSession session, IccAssociationBackup backup, GammaDeviceState current)
    {
        VerifyAssociationTarget(session, backup, current);
        var adapter = Adapter(backup.TargetKey);
        string? currentUser = ReadDefault(adapter, backup.SourceId, 1);
        int currentScope = ReadScope(adapter, backup.SourceId);
        if (currentScope == 0 && backup.Scope == 1)
            throw new ProfilePreviewException("external_profile_change", "Another application changed the color profile scope.");
        if (ProfilePreviewPolicy.EqualName(currentUser, backup.CandidateName))
        {
            if (backup.UserDefault is not null)
            {
                IccProfileInspector.ValidateFileName(backup.UserDefault);
                Check(ColorProfileSetDisplayDefaultAssociation(1, backup.UserDefault, 0, 4, adapter, backup.SourceId));
            }
        }
        else if (!ProfilePreviewPolicy.EqualName(currentUser, backup.UserDefault)
            && !ProfilePreviewPolicy.EqualName(currentUser, backup.EffectiveDefault))
            throw new ProfilePreviewException("external_profile_change", "Another application selected a different profile during recovery.");
        var associations = WindowsProfileAssociationBackend.List(backup.TargetKey, backup.SourceId, 1);
        if (associations.Contains(backup.CandidateName, StringComparer.OrdinalIgnoreCase))
            Check(ColorProfileRemoveDisplayAssociation(1, backup.CandidateName, adapter, backup.SourceId, false));
        if (currentScope != backup.Scope) SetUserScope(backup.DeviceKey, backup.Scope == 1);
        int restoredScope = ReadScope(adapter, backup.SourceId);
        string? restoredUserDefault = ReadDefault(adapter, backup.SourceId, 1);
        bool defaultRestored = ProfilePreviewPolicy.EqualName(restoredUserDefault, backup.UserDefault)
            || (backup.UserDefault is null && ProfilePreviewPolicy.EqualName(restoredUserDefault, backup.EffectiveDefault));
        if (restoredScope != backup.Scope || !defaultRestored)
            throw new IOException("The original color profile association or scope was not restored.");
    }

    private IccAssociationBackup ReadAssociationBackup(GammaCalibrationSession session)
    {
        string path = Path.Combine(_store.DirectoryFor(session.Id), "association.json");
        var backup = ProfilePreviewStore.Read<IccAssociationBackup>(path);
        if (backup.TargetKey != session.Original.TargetKey || backup.Scope is not (0 or 1)
            || backup.CandidateName != "DCCWPlus-" + session.Id.ToString("N") + ".icc"
            || backup.DeviceKey?.StartsWith(@"\Registry\Machine\System\", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidDataException("Invalid ICC association recovery record.");
        return backup;
    }

    private static void VerifyAssociationTarget(GammaCalibrationSession session, IccAssociationBackup backup, GammaDeviceState current)
    {
        var device = new WindowsDisplayService().Enumerate().SingleOrDefault(d => d.Id == current.DisplayId);
        if (!GammaCalibrationTransaction.SameRecoveryTarget(session.Original, current) || current.SourceId != backup.SourceId
            || device is null || device.TargetKey != backup.TargetKey || device.SourceId != backup.SourceId
            || !string.Equals(device.ProfileDeviceKey, backup.DeviceKey, StringComparison.OrdinalIgnoreCase))
            throw new ProfilePreviewException("recovery_target_changed", "The original ICC association target is unavailable.");
    }

    private static GammaRamp ReadRamp(string deviceName)
    {
        using var dc = DisplayDc.Create(deviceName);
        var native = NativeRamp.Empty();
        if (!GetDeviceGammaRamp(dc.Handle, ref native)) throw new ProfilePreviewException("curve_read_failed", "The display driver does not expose its gamma table.");
        var ramp = new GammaRamp(native.Red, native.Green, native.Blue); ramp.Validate(); return ramp;
    }
    private static void Check(int hr) { if (hr < 0) throw new ProfilePreviewException("profile_write_failed", $"Windows color profile operation failed (0x{hr:X8})."); }
    private static void SetUserScope(string deviceKey, bool perUser)
    {
        if (!WcsSetUsePerUserProfiles(deviceKey, 0x6D6E7472, perUser))
            throw new ProfilePreviewException("profile_scope_write_failed", $"Windows could not change the profile scope (Win32 {Marshal.GetLastWin32Error()}).");
    }
    private sealed class DisplayDc(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;
        public static DisplayDc Create(string name)
        {
            if (!name.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase) || name.Length > 32)
                throw new InvalidDataException("Invalid GDI display name.");
            IntPtr handle = CreateDC("DISPLAY", name, null, IntPtr.Zero);
            if (handle == IntPtr.Zero) throw new IOException("Cannot open the display device context.");
            return new(handle);
        }
        public void Dispose() => DeleteDC(Handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct VcgtCapabilities { public uint Size; public int Supported; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRamp
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Blue;
        public static NativeRamp Empty() => new() { Red = new ushort[256], Green = new ushort[256], Blue = new ushort[256] };
        public static NativeRamp From(GammaRamp ramp) => new() { Red = ramp.Red, Green = ramp.Green, Blue = ramp.Blue };
    }
    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr init);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDeviceGammaRamp(IntPtr dc, ref NativeRamp ramp);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetDeviceGammaRamp(IntPtr dc, ref NativeRamp ramp);
    [DllImport("mscms.dll")] private static extern int ColorProfileGetDeviceCapabilities(int scope, Luid adapter, uint source, int type, ref VcgtCapabilities caps);
    [DllImport("mscms.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WcsGetCalibrationManagementState(out int enabled);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WcsSetUsePerUserProfiles(string device, uint deviceClass, [MarshalAs(UnmanagedType.Bool)] bool usePerUser);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern int ColorProfileAddDisplayAssociation(int scope, string name, Luid adapter, uint source, [MarshalAs(UnmanagedType.Bool)] bool setDefault, [MarshalAs(UnmanagedType.Bool)] bool advanced);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern int ColorProfileRemoveDisplayAssociation(int scope, string name, Luid adapter, uint source, [MarshalAs(UnmanagedType.Bool)] bool advanced);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern int ColorProfileSetDisplayDefaultAssociation(int scope, string name, int type, int subtype, Luid adapter, uint source);
    [DllImport("mscms.dll", EntryPoint = "InstallColorProfileW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InstallColorProfile(string? machine, string path);
}
