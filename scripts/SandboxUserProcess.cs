using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// Loaded by the Windows PowerShell runner inside a disposable Windows Sandbox.
public static class SandboxUserProcess
{
    private const int InteractiveLogon = 2;
    private const int ProfileNoUi = 1;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public short Show, ReservedBytes;
        public IntPtr ReservedPointer, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProfileInfo
    {
        public int Size, Flags;
        public string User, ProfilePath, DefaultPath, ServerName, PolicyPath;
        public IntPtr Profile;
    }

    public static uint Run(string user, string password, string application, string arguments, string directory)
    {
        IntPtr token;
        if (!LogonUserW(user, ".", password, InteractiveLogon, 0, out token)) throw NativeError("LogonUserW");
        try
        {
            var profile = new ProfileInfo { Size = Marshal.SizeOf(typeof(ProfileInfo)), Flags = ProfileNoUi, User = user };
            if (!LoadUserProfileW(token, ref profile)) throw NativeError("LoadUserProfileW");
            IntPtr environment = IntPtr.Zero;
            try
            {
                if (!CreateEnvironmentBlock(out environment, token, false)) throw NativeError("CreateEnvironmentBlock");
                // An empty desktop selects the window station for the new logon session.
                var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = "" };
                ProcessInfo info;
                var command = new StringBuilder("\"" + application + "\" " + arguments);
                if (!CreateProcessAsUserW(token, application, command, IntPtr.Zero, IntPtr.Zero, false,
                    CreateNoWindow | CreateUnicodeEnvironment, environment, directory, ref startup, out info))
                    throw NativeError("CreateProcessAsUserW");
                try
                {
                    uint wait = WaitForSingleObject(info.Process, 90000);
                    if (wait == 258) throw new TimeoutException("The standard-user check did not finish.");
                    if (wait != 0) throw NativeError("WaitForSingleObject");
                    uint exitCode;
                    if (!GetExitCodeProcess(info.Process, out exitCode)) throw NativeError("GetExitCodeProcess");
                    return exitCode;
                }
                finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
            }
            finally
            {
                if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
                UnloadUserProfile(token, profile.Profile);
            }
        }
        finally { CloseHandle(token); }
    }

    private static Win32Exception NativeError(string operation)
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, operation + " failed (Win32 " + code + ").");
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool LogonUserW(string user, string domain, string password, int type, int provider, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool LoadUserProfileW(IntPtr token, ref ProfileInfo profile);
    [DllImport("userenv.dll")] private static extern bool UnloadUserProfile(IntPtr token, IntPtr profile);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
}
