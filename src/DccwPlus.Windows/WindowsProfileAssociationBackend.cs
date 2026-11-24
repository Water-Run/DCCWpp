using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DccwPlus.Core.Calibration;

namespace DccwPlus.Windows;

// Deliberately separate from the read-only display probe. Only the preview worker calls SetDefault.
public sealed class WindowsProfileAssociationBackend : IProfileAssociationBackend
{
    private const int CurrentUser = 1, Icc = 0, StandardDisplay = 4;
    public ProfileAssociationState Read(string displayId)
    {
        var displays = new WindowsDisplayService().Enumerate();
        var display = displays.SingleOrDefault(d => d.Id == displayId)
            ?? throw new ProfilePreviewException("display_disconnected", "The display is no longer connected.");
        if (display.TargetKey is null || display.SourceId is null)
            throw new ProfilePreviewException("display_identity_unavailable", "Windows did not report a display source identity.");
        var adapter = Adapter(display.TargetKey);
        Check(ColorProfileGetDisplayUserScope(adapter, display.SourceId.Value, out int scope), "profile_scope_unavailable");
        if (scope is not (0 or 1)) throw new ProfilePreviewException("profile_scope_unavailable", "Unknown profile scope.");
        string? current = Default(adapter, display.SourceId.Value, scope);
        var profiles = List(adapter, display.SourceId.Value, scope).Select(Inspect).ToArray();
        string topology = string.Join('\n', displays.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d =>
            FormattableString.Invariant($"{d.Id}|{d.TargetKey}|{d.SourceId}|{d.Topology}|{d.ColorState}|{d.HdrActive}|{d.AdvancedColorActive}|{d.Width}|{d.Height}|{d.PositionX}|{d.PositionY}")));
        return new(display.Id, display.TargetKey, display.SourceId.Value,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(topology))), current,
            scope == CurrentUser ? "current-user" : "system-wide", CalibrationService.IsExplicitSdr(display),
            display.Topology == "clone" || displays.Count(d => d.DeviceName == display.DeviceName) > 1, profiles);
    }

    public void SetDefault(ProfileAssociationState expected, string profile, string sha256)
    {
        IccProfileInspector.ValidateFileName(profile);
        var now = Read(expected.DisplayId);
        if (!ProfilePreviewPolicy.SameTarget(expected, now)
            || !ProfilePreviewPolicy.EqualName(expected.CurrentProfile, now.CurrentProfile))
            throw new ProfilePreviewException("display_state_changed", "The display changed before the profile could be selected.");
        if (ProfilePreviewPolicy.ValidProfile(now, profile)?.Sha256 != sha256)
            throw new ProfilePreviewException("profile_file_changed", "The associated profile is missing, invalid, or has changed.");
        Check(ColorProfileSetDisplayDefaultAssociation(CurrentUser, profile, Icc, StandardDisplay,
            Adapter(now.TargetKey), now.SourceId), "profile_write_failed");
    }

    private static AssociatedProfile Inspect(string fileName)
    {
        try
        {
            byte[] bytes = InstalledProfileReader.ReadBytes(fileName);
            var report = IccProfileInspector.Inspect(fileName, bytes);
            bool valid = report.ReadStatus == "read" && report.SignatureValid && report.DeviceClass == "mntr"
                && report.ColorSpace == "RGB" && report.VcgtPresent is not null && report.TrcPresent is not null;
            return new(fileName, Convert.ToHexString(SHA256.HashData(bytes)), valid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { return new(fileName, null, false); }
    }

    internal static IReadOnlyList<string> List(string targetKey, uint source, int scope) => List(Adapter(targetKey), source, scope);

    private static IReadOnlyList<string> List(Luid adapter, uint source, int scope)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int result = ColorProfileGetDisplayList(scope, adapter, source, out buffer, out uint count);
            if (result is unchecked((int)0x80070490) or unchecked((int)0x80070002)) return [];
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

    private static string? Default(Luid adapter, uint source, int scope)
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            int result = ColorProfileGetDisplayDefault(scope, adapter, source, Icc, StandardDisplay, out pointer);
            // No usable default means there is no verified rollback destination. Never fall back to another scope.
            if (result < 0 || pointer == IntPtr.Zero) return null;
            string? value = Marshal.PtrToStringUni(pointer);
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = Path.GetFileName(value);
            IccProfileInspector.ValidateFileName(value);
            return value;
        }
        finally { if (pointer != IntPtr.Zero) LocalFree(pointer); }
    }
    private static Luid Adapter(string target)
    {
        string[] parts = target.Split(':');
        if (parts.Length != 3) throw new InvalidDataException("Invalid adapter identity.");
        return new() { High = unchecked((int)uint.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
            Low = uint.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture) };
    }
    private static void Check(int result, string code)
    { if (result < 0) throw new ProfilePreviewException(code, $"Windows profile operation failed (0x{result:X8})."); }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [DllImport("mscms.dll")] private static extern int ColorProfileGetDisplayUserScope(Luid adapter, uint source, out int scope);
    [DllImport("mscms.dll")] private static extern int ColorProfileGetDisplayList(int scope, Luid adapter, uint source, out IntPtr list, out uint count);
    [DllImport("mscms.dll")] private static extern int ColorProfileGetDisplayDefault(int scope, Luid adapter, uint source, int type, int subtype, out IntPtr name);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)] private static extern int ColorProfileSetDisplayDefaultAssociation(int scope, string name, int type, int subtype, Luid adapter, uint source);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
