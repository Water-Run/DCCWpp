using System.Globalization;
using System.Runtime.InteropServices;
using DccwPlus.Core.Calibration;

namespace DccwPlus.Windows;

internal static class WindowsColorProfileQuery
{
    internal const int SystemWide = 0, CurrentUser = 1, Icc = 0, StandardDisplay = 4;

    internal static EffectiveProfile TryReadEffective(Luid adapter, uint source, int subtype)
    {
        int? selected = null;
        try
        {
            selected = ReadScope(adapter, source);
            var profile = ReadEffective(selected.Value, scope => ReadDefault(adapter, source, scope, subtype));
            return ReadScope(adapter, source) == selected ? profile : new(null, null, null, "unavailable");
        }
        catch (ProfilePreviewException)
        {
            return new(null, null, selected is int scope ? ScopeName(scope) : null, "unavailable");
        }
    }

    internal static EffectiveProfile ReadEffective(int selected, Func<int, string?> readDefault)
    {
        string? name = readDefault(selected);
        string? effectiveScope = name is null ? null : ScopeName(selected);
        if (name is null && selected == CurrentUser)
        {
            name = readDefault(SystemWide);
            if (name is not null) effectiveScope = ScopeName(SystemWide);
        }
        return new(name, effectiveScope, ScopeName(selected), name is null ? "not-found" : "read");
    }

    internal static int ReadScope(Luid adapter, uint source)
    {
        int result = ColorProfileGetDisplayUserScope(adapter, source, out int scope);
        if (result < 0 || scope is not (SystemWide or CurrentUser))
            throw new ProfilePreviewException("profile_scope_unavailable", $"Windows did not report the selected profile scope (0x{result:X8}).");
        return scope;
    }

    internal static string? ReadDefault(Luid adapter, uint source, int scope, int subtype = StandardDisplay)
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            int result = ColorProfileGetDisplayDefault(scope, adapter, source, Icc, subtype, out pointer);
            return DecodeDefault(result, result >= 0 && pointer != IntPtr.Zero ? Marshal.PtrToStringUni(pointer) : null);
        }
        finally { if (pointer != IntPtr.Zero) LocalFree(pointer); }
    }

    internal static string? DecodeDefault(int result, string? name)
    {
        if (IsAbsent(result)) return null;
        if (result < 0)
            throw new ProfilePreviewException("profile_state_unavailable", $"Windows could not read the default profile (0x{result:X8}).");
        try
        {
            string fileName = Path.GetFileName(name) ?? "";
            IccProfileInspector.ValidateFileName(fileName);
            return fileName;
        }
        catch (InvalidDataException)
        {
            throw new ProfilePreviewException("profile_state_unavailable", "Windows returned an invalid default profile name.");
        }
    }

    internal static bool IsAbsent(int result) => result is unchecked((int)0x80070490) or unchecked((int)0x80070002);
    internal static string ScopeName(int scope) => scope == CurrentUser ? "current-user" : "system-wide";

    internal static Luid Adapter(string target)
    {
        string[] parts = target.Split(':');
        if (parts.Length != 3) throw new InvalidDataException("Invalid display adapter identity.");
        return new()
        {
            High = unchecked((int)uint.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
            Low = uint.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        };
    }

    internal readonly record struct EffectiveProfile(string? Name, string? Scope, string? SelectedScope, string? Status);
    [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint Low; public int High; }
    [DllImport("mscms.dll")] private static extern int ColorProfileGetDisplayUserScope(Luid adapter, uint source, out int scope);
    [DllImport("mscms.dll")] private static extern int ColorProfileGetDisplayDefault(int scope, Luid adapter, uint source, int type, int subtype, out IntPtr name);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
