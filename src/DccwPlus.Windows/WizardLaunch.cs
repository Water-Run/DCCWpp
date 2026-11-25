using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DccwPlus.Core.Calibration;
using DccwPlus.Core.Localization;
using Microsoft.Win32.SafeHandles;

namespace DccwPlus.Windows;

public sealed record WizardLaunchRequest(Guid Id, string? DisplayId, string? DisplayStamp, Guid? PresetId)
{
    public const string Marker = "--dccwpp-wizard";
    internal string PipeName => $"DCCWPlus.Wizard.{Id:N}";

    internal string[] Arguments() =>
        [Marker, Id.ToString("N"), DisplayId ?? "-", DisplayStamp ?? "-", PresetId?.ToString("D") ?? "-"];

    public static WizardLaunchRequest? Parse(string[] arguments)
    {
        if (arguments is not [Marker, var idText, var display, var stamp, var preset]
            || !Guid.TryParseExact(idText, "N", out var id) || id == Guid.Empty
            || (display == "-" ? stamp != "-" : !IsDisplayId(display) || !ProfilePreviewPolicy.IsHash(stamp)))
            return null;
        Guid? presetId = null;
        if (preset != "-")
        {
            if (!Guid.TryParseExact(preset, "D", out var parsed) || parsed == Guid.Empty) return null;
            presetId = parsed;
        }
        return new(id, display == "-" ? null : display, stamp == "-" ? null : stamp, presetId);
    }

    public static bool IsDisplayId(string value) => value.Length == 16 && value.All(Uri.IsHexDigit);

    // Names, positions and color modes can change without reusing the active path for a different monitor.
    public static string Stamp(DisplayInfo display) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        display.Id, display.DeviceName, display.TargetKey, display.SourceId, display.PersistenceKey,
        ProfileDevice = display.ProfileDeviceKey?.ToUpperInvariant()
    })));

    public bool Matches(DisplayInfo display) => string.Equals(DisplayId, display.Id, StringComparison.Ordinal)
        && string.Equals(DisplayStamp, Stamp(display), StringComparison.Ordinal);
}

public enum WizardLaunchOutcome : byte
{
    Opened = 1, DisplayUnavailable, DisplayChanged, PlacementFailed, PresetUnavailable, Failed
}

public static class WizardLaunch
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(18);
    private const string Executable = "DccwPlus.App.exe";
    private const PipeOptions PipeMode = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    public static bool IsAvailable() => string.Equals(new DirectoryInfo(AppContext.BaseDirectory).Name, "Cli", StringComparison.OrdinalIgnoreCase)
        && File.Exists(AdjacentApp());

    public static async Task<uint> OpenAsync(WizardLaunchRequest request)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        using var pipe = new NamedPipeServerStream(request.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeMode);
        uint processId;
        try
        {
            processId = await Task.Run(() => Activate(request)).WaitAsync(timeout.Token);
        }
        catch (Win32Exception)
        {
            throw Failure("wizard_launch_failed", "WizardLaunchFailed");
        }
        catch (OperationCanceledException)
        {
            throw Failure("wizard_launch_timeout", "WizardLaunchTimeout");
        }

        byte[] reply = new byte[1];
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint client) || client != processId)
                throw Failure("wizard_response_invalid", "WizardLaunchFailed");
            await pipe.ReadExactlyAsync(reply, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw Failure("wizard_launch_timeout", "WizardLaunchTimeout");
        }
        catch (IOException)
        {
            throw Failure("wizard_response_invalid", "WizardLaunchFailed");
        }
        return (WizardLaunchOutcome)reply[0] switch
        {
            WizardLaunchOutcome.Opened => processId,
            WizardLaunchOutcome.DisplayUnavailable => throw Failure("display_disconnected", "DisplayUnavailable"),
            WizardLaunchOutcome.DisplayChanged => throw Failure("display_state_changed", "DisplaySelectionLost"),
            WizardLaunchOutcome.PlacementFailed => throw Failure("wizard_placement_failed", "WizardPlacementFailed"),
            WizardLaunchOutcome.PresetUnavailable => throw Failure("wizard_preset_unavailable", "WizardPresetUnavailable"),
            WizardLaunchOutcome.Failed => throw Failure("wizard_launch_failed", "WizardLaunchFailed"),
            _ => throw Failure("wizard_response_invalid", "WizardLaunchFailed")
        };
    }

    public static async Task RespondAsync(WizardLaunchRequest request, WizardLaunchOutcome outcome)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var pipe = new NamedPipeClientStream(".", request.PipeName, PipeDirection.Out, PipeMode);
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(new byte[] { (byte)outcome }, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Debug.WriteLine(ex);
        }
    }

    private static uint Activate(WizardLaunchRequest request)
    {
        string path = AdjacentApp();
        var arguments = new StringBuilder($"\"{path}\" {string.Join(' ', request.Arguments())}");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        // Both executables belong to the same full-trust package. Launch in the caller's context and leave its
        // redirected streams with the CLI so headless callers can collect JSON while WinUI remains open.
        if (!CreateProcess(path, arguments, 0, 0, false, 0, 0, Path.GetDirectoryName(path)!, ref startup, out var child))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var processHandle = new SafeProcessHandle(child.Process, ownsHandle: true);
        using var threadHandle = new SafeWaitHandle(child.Thread, ownsHandle: true);
        return child.ProcessId;
    }

    private static string AdjacentApp() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", Executable));

    private static ProfilePreviewException Failure(string code, string messageKey) => new(code, new LocalizationService()[messageKey]);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, nint processSecurity,
        nint threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment,
        string workingDirectory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
