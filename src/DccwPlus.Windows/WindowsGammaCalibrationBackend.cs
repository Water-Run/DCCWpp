using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;
using DccwPlus.Core.Presets;
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
        return ReadSnapshot(display, displays);
    }

    private static GammaDeviceState ReadSnapshot(DisplayInfo display, IReadOnlyList<DisplayInfo> displays)
    {
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

    internal GammaDeviceState CheckPreviewStart(string displayId, CalibrationPreset preset)
    {
        PresetCodec.Validate(preset);
        var displays = new WindowsDisplayService().Enumerate();
        var device = displays.SingleOrDefault(d => d.Id == displayId)
            ?? throw new ProfilePreviewException("display_disconnected", new LocalizationService()["DisplayUnavailable"]);
        if (SdrBlockReason(device, displays) is { } blocked)
            throw new ProfilePreviewException(blocked, new LocalizationService()[blocked switch
            {
                "sdr_path_blocked" => "SdrRequired",
                "mhc2_profile_blocked" => "Mhc2ProfileBlocked",
                "shared_source_blocked" => "MirroredDisplay",
                "profile_state_unavailable" => "ProfileStateUnavailable",
                _ => "DisplayUnavailable"
            }]);
        GammaDeviceState current;
        try { current = ReadSnapshot(device, displays); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ProfilePreviewException { Code: "curve_read_failed" })
        {
            throw new ProfilePreviewException("curve_read_failed", new LocalizationService()["CurveReadUnavailable"]);
        }
        var ramp = GammaRampMath.Compose(current.Ramp, preset.Calibration);
        try { ramp.Validate(); }
        catch (InvalidDataException)
        {
            throw new ProfilePreviewException("curve_candidate_invalid", new LocalizationService()["CurveCandidateInvalid"]);
        }
        var adapter = Adapter(device.TargetKey!);
        uint source = device.SourceId!.Value;
        _ = PrepareIcc(device, current, ramp, Guid.Empty, InstalledProfileReader.ReadBytes,
            scope => ReadDefault(adapter, source, scope), () => ReadScope(adapter, source));
        return current;
    }
    public SdrCalibrationDiagnostics Diagnose(string displayId)
    {
        var displays = new WindowsDisplayService().Enumerate();
        var display = displays.SingleOrDefault(d => d.Id == displayId)
            ?? throw new KeyNotFoundException("The requested display is not connected.");
        bool? loader = WcsGetCalibrationManagementState(out int enabled) ? enabled != 0 : null;
        bool? vcgt = null;
        bool? readable = null;
        string? blocked = SdrBlockReason(display, displays);
        string reason = blocked ?? "logical_ramp_readable";
        if (blocked is null)
        {
            var adapter = Adapter(display.TargetKey!);
            uint source = display.SourceId!.Value;
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

    internal static string? SdrBlockReason(DisplayInfo display, IReadOnlyList<DisplayInfo> displays)
    {
        if (!CalibrationService.IsExplicitSdr(display)) return "sdr_path_blocked";
        if (display.Profile?.Mhc2Present == true) return "mhc2_profile_blocked";
        if (display.ProfileQueryStatus == "unavailable") return "profile_state_unavailable";
        if (display.TargetKey is null || display.SourceId is null) return "display_identity_unavailable";
        return display.Topology == "clone" || displays.Count(d => d.DeviceName == display.DeviceName) > 1
            ? "shared_source_blocked" : null;
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
        var displays = new WindowsDisplayService().Enumerate();
        var device = displays.SingleOrDefault(d => d.Id == session.Original.DisplayId)
            ?? throw new ProfilePreviewException("display_disconnected", new LocalizationService()["DisplayUnavailable"]);
        var now = ReadSnapshot(device, displays);
        if (!GammaCalibrationTransaction.SameTarget(session.Original, now) || !GammaCalibrationTransaction.SameProfile(session.Original, now)
            || !now.Ramp.Matches(session.Candidate, 257))
            throw new ProfilePreviewException("display_state_changed", "The display changed before the calibration could be saved.");
        var adapter = Adapter(device.TargetKey!);
        uint source = device.SourceId!.Value;
        var (backup, profile) = PrepareIcc(device, now, session.Candidate, session.Id, InstalledProfileReader.ReadBytes,
            scope => ReadDefault(adapter, source, scope), () => ReadScope(adapter, source));
        string candidate = backup.CandidateName;
        string directory = _store.DirectoryFor(session.Id);
        string backupPath = Path.Combine(directory, "association.json");
        AtomicFile.Write(backupPath, JsonSerializer.SerializeToUtf8Bytes(backup, ProfilePreviewStore.Json), overwrite: false);
        string path = Path.Combine(directory, candidate);
        AtomicFile.Write(path, profile, overwrite: false);
        if (!InstallColorProfile(null, path))
            throw new ProfilePreviewException("profile_install_failed", $"Windows could not install the calibration profile (Win32 {Marshal.GetLastWin32Error()}).");
        VerifyProfileFingerprint(candidate, backup.CandidateHash, InstalledProfileReader.ReadSha256);
        VerifyOriginalFingerprints(backup, InstalledProfileReader.ReadSha256);
        var beforeAssociation = Read(device.Id);
        int beforeScope = ReadScope(adapter, source);
        if (!GammaCalibrationTransaction.SameTarget(session.Original, beforeAssociation)
            || !GammaCalibrationTransaction.SameProfile(session.Original, beforeAssociation)
            || beforeScope != backup.Scope || !ProfilePreviewPolicy.EqualName(ReadDefault(adapter, source, CurrentUser), backup.UserDefault)
            || !beforeAssociation.Ramp.Matches(session.Candidate, 257))
            throw new ProfilePreviewException("display_state_changed", "The display changed while the calibration profile was being installed.");
        AtomicFile.Write(Path.Combine(directory, "association-mutation-started"), []);
        if (backup.Scope != CurrentUser) SetUserScope(backup.DeviceKey, true);
        Check(ColorProfileAddDisplayAssociation(1, candidate, adapter, source, true, false));
        var associated = Read(device.Id);
        if (!GammaCalibrationTransaction.SameTarget(session.Original, associated)
            || associated.ProfileScope != "current-user" || !ProfilePreviewPolicy.EqualName(associated.ProfileName, candidate))
            throw new ProfilePreviewException("association_not_verified", "Windows did not report the saved calibration profile as default.");
        VerifyProfileFingerprint(candidate, backup.CandidateHash, InstalledProfileReader.ReadSha256);
        // Profile notification may reload calibration. Reapply only this same confirmed curve.
        if (!associated.Ramp.Matches(session.Candidate, 257)) Write(associated, session.Candidate);
        return candidate;
    }

    internal static (IccAssociationBackup Backup, byte[] Profile) PrepareIcc(DisplayInfo device, GammaDeviceState current,
        GammaRamp ramp, Guid id, Func<string, byte[]> readFile, Func<int, string?> readDefault, Func<int> readScope)
    {
        if (device.ProfileDeviceKey is not { } deviceKey
            || !deviceKey.StartsWith(@"\Registry\Machine\System\", StringComparison.OrdinalIgnoreCase))
            throw new ProfilePreviewException("profile_device_unavailable", new LocalizationService()["ProfileDeviceUnavailable"]);
        int scope = readScope();
        string? userDefault = readDefault(CurrentUser);
        string? effective = (scope == CurrentUser ? userDefault : null) ?? readDefault(SystemWide);
        if (current.SelectedProfileScope != ScopeName(scope) || !ProfilePreviewPolicy.EqualName(effective, current.ProfileName)
            || readScope() != scope)
            throw new ProfilePreviewException("external_profile_change", new LocalizationService()["ProfileChangedBeforeSave"]);
        string baseName = effective ?? "sRGB Color Space Profile.icm";
        byte[] original = ReadIccForSave(baseName, readFile);
        byte[] profile;
        try { profile = IccCalibrationWriter.Build(original, ramp, id); }
        catch (InvalidDataException)
        {
            throw new ProfilePreviewException("icc_base_unsupported", new LocalizationService()["IccBaseUnsupported"]);
        }
        string? effectiveHash = effective is null ? null : Convert.ToHexString(SHA256.HashData(original));
        string? userHash = userDefault is null ? null : ProfilePreviewPolicy.EqualName(userDefault, effective) ? effectiveHash
            : Convert.ToHexString(SHA256.HashData(ReadIccForSave(userDefault, readFile)));
        var backup = new IccAssociationBackup(scope, userDefault, device.SourceId!.Value, device.TargetKey!,
            $"DCCWPlus-{id:N}.icc", deviceKey, effective, effectiveHash, userHash, Convert.ToHexString(SHA256.HashData(profile)));
        return (backup, profile);
    }

    private static byte[] ReadIccForSave(string name, Func<string, byte[]> readFile)
    {
        try { return readFile(name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new ProfilePreviewException("profile_file_unavailable", new LocalizationService()["IccFileUnavailable"]);
        }
    }

    internal string ReadConfirmedProfileHash(GammaCalibrationSession session)
    {
        var backup = ReadAssociationBackup(session);
        if (!ProfilePreviewPolicy.EqualName(backup.CandidateName, session.InstalledProfile))
            throw new InvalidDataException("Invalid ICC association recovery record.");
        return VerifyProfileFingerprint(backup.CandidateName, backup.CandidateHash, InstalledProfileReader.ReadSha256);
    }

    public void RestoreProfile(GammaCalibrationSession session)
    {
        string directory = _store.DirectoryFor(session.Id);
        var current = Read(session.Original.DisplayId);
        if (!File.Exists(Path.Combine(directory, "association-mutation-started")))
        {
            if (!GammaCalibrationTransaction.SameProfile(session.Original, current))
                throw new ProfilePreviewException("external_profile_change", "Another application selected a different profile.");
            if (session.UndoOf is Guid beforeUndo)
            {
                var saved = ReadAssociationBackup(_store.Load(beforeUndo));
                if (saved.CandidateHash is not null)
                    VerifyProfileFingerprint(saved.CandidateName, saved.CandidateHash, InstalledProfileReader.ReadSha256);
            }
            else if (File.Exists(Path.Combine(directory, "association.json")))
            {
                var original = ReadAssociationBackup(session);
                if (original.EffectiveDefaultHash is not null)
                    VerifyProfileFingerprint(original.EffectiveDefault, original.EffectiveDefaultHash, InstalledProfileReader.ReadSha256);
            }
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
        bool candidateIsDefault = ProfilePreviewPolicy.EqualName(currentUser, backup.CandidateName);
        if (!candidateIsDefault && !ProfilePreviewPolicy.EqualName(currentUser, backup.UserDefault)
            && !ProfilePreviewPolicy.EqualName(currentUser, backup.EffectiveDefault))
            throw new ProfilePreviewException("external_profile_change", "Another application selected a different profile during recovery.");
        if (!PreviousSystemDefaultMatches(backup, session.Original.ProfileName, () => ReadDefault(adapter, backup.SourceId, SystemWide)))
            throw new ProfilePreviewException("previous_system_profile_changed", new LocalizationService()["OriginalSystemProfileChanged"]);

        var associations = WindowsProfileAssociationBackend.List(backup.TargetKey, backup.SourceId, 1);
        bool candidateIsAssociated = associations.Contains(backup.CandidateName, StringComparer.OrdinalIgnoreCase);
        VerifyOriginalFingerprints(backup, InstalledProfileReader.ReadSha256);
        if ((candidateIsAssociated || candidateIsDefault) && backup.CandidateHash is not null)
            VerifyProfileFingerprint(backup.CandidateName, backup.CandidateHash, InstalledProfileReader.ReadSha256);

        if (candidateIsDefault && backup.UserDefault is not null)
        {
            IccProfileInspector.ValidateFileName(backup.UserDefault);
            Check(ColorProfileSetDisplayDefaultAssociation(1, backup.UserDefault, 0, 4, adapter, backup.SourceId));
        }
        if (candidateIsAssociated)
            Check(ColorProfileRemoveDisplayAssociation(1, backup.CandidateName, adapter, backup.SourceId, false));
        if (currentScope != backup.Scope) SetUserScope(backup.DeviceKey, backup.Scope == 1);
        int restoredScope = ReadScope(adapter, backup.SourceId);
        string? restoredUserDefault = ReadDefault(adapter, backup.SourceId, 1);
        bool defaultRestored = ProfilePreviewPolicy.EqualName(restoredUserDefault, backup.UserDefault)
            || (backup.UserDefault is null && ProfilePreviewPolicy.EqualName(restoredUserDefault, backup.EffectiveDefault));
        if (restoredScope != backup.Scope || !defaultRestored)
            throw new IOException("The original color profile association or scope was not restored.");
    }

    internal static bool PreviousSystemDefaultMatches(IccAssociationBackup backup, string? originalProfile, Func<string?> readSystemDefault) =>
        (backup.Scope == CurrentUser && backup.UserDefault is not null)
        || ProfilePreviewPolicy.EqualName(originalProfile, readSystemDefault());

    internal static void VerifyOriginalFingerprints(IccAssociationBackup backup, Func<string, string> readHash)
    {
        // Older interrupted journals may omit fingerprints. Apply every fingerprint that was recorded.
        if (backup.EffectiveDefaultHash is not null)
            VerifyProfileFingerprint(backup.EffectiveDefault, backup.EffectiveDefaultHash, readHash);
        if (backup.UserDefaultHash is not null && !(ProfilePreviewPolicy.EqualName(backup.UserDefault, backup.EffectiveDefault)
            && string.Equals(backup.UserDefaultHash, backup.EffectiveDefaultHash, StringComparison.OrdinalIgnoreCase)))
            VerifyProfileFingerprint(backup.UserDefault, backup.UserDefaultHash, readHash);
    }

    internal static string VerifyProfileFingerprint(string? profileName, string? hash, Func<string, string> readHash)
    {
        if (profileName is null || !ProfilePreviewPolicy.IsHash(hash))
            throw new InvalidDataException("Invalid ICC association recovery record.");

        string actual;
        try { actual = readHash(profileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new ProfilePreviewException("profile_file_unavailable", new LocalizationService()["IccFileUnavailable"]);
        }
        if (!string.Equals(hash, actual, StringComparison.OrdinalIgnoreCase))
            throw new ProfilePreviewException("profile_file_changed", new LocalizationService()["IccFileChanged"]);
        return actual;
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

    internal static GammaRamp ReadRamp(string deviceName)
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
