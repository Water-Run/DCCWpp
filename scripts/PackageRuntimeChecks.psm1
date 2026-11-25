Set-StrictMode -Version Latest

function Invoke-DccwAgentCli {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Command,
        [string]$DataDirectory,
        [int]$ExpectedExitCode = 0
    )

    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $arguments = $Command + @('--json')
    if ($DataDirectory) { $arguments += @('--data-dir', $DataDirectory) }
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }

    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(25000)) {
            if (!$process.HasExited) { $process.Kill() }
            throw "CLI request timed out: $($Command -join ' ')"
        }
        $remaining = [int][Math]::Max(0, 25000 - $timer.ElapsedMilliseconds)
        if (![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), $remaining)) {
            throw "CLI output remained open after the command exited: $($Command -join ' ')"
        }
        $response = $stdout.Result | ConvertFrom-Json -ErrorAction Stop
        if ($process.ExitCode -ne $ExpectedExitCode -or $stderr.Result -or
            $response.schemaVersion -ne 1 -or $response.ok -ne ($ExpectedExitCode -eq 0)) {
            throw "Unexpected CLI result for $($Command -join ' '): $($stdout.Result) $($stderr.Result)"
        }
        return $response
    }
    finally { $process.Dispose() }
}

function Test-DccwWizardHandoff {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$InstalledPackage,
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][guid]$PresetId,
        [ValidatePattern('^[0-9a-fA-F]{16}$')][string]$DisplayId,
        [switch]$RequireNonElevated
    )

    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    $elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (![Environment]::UserInteractive -or ($RequireNonElevated -and $elevated)) {
        throw 'Run the handoff check from the requested interactive Windows desktop and user context.'
    }
    $architecture = $InstalledPackage.Architecture.ToString().ToLowerInvariant()
    $expectedMachine = switch ($architecture) {
        'arm64' { 0xaa64 }
        'x64' { 0x8664 }
        default { throw 'The installed package must be x64 or ARM64.' }
    }
    if (!(Invoke-DccwAgentCli $Executable @('status')).data.packaged) {
        throw 'The selected executable did not run inside a package.'
    }
    if (!$DisplayId) {
        $display = (Invoke-DccwAgentCli $Executable @('displays', 'list')).data |
            Where-Object { $_.deviceName.StartsWith('\\.\DISPLAY', [StringComparison]::OrdinalIgnoreCase) } |
            Sort-Object isPrimary | Select-Object -First 1
        if ($display) { $DisplayId = $display.id }
    }
    $sessionsBefore = @((Invoke-DccwAgentCli $Executable @('calibration', 'sessions')).data |
        ForEach-Object { $_.sessionId } | Sort-Object)
    $wizardArguments = @('wizard', 'open', '--preset', $PresetId.ToString('D'))
    if ($DisplayId) { $wizardArguments += @('--display', $DisplayId) }
    $plan = (Invoke-DccwAgentCli $Executable ($wizardArguments + '--dry-run')).data
    if (!$plan.canOpen -or $plan.reasonCode -ne 'wizard_ready' -or $plan.changesDisplay -or
        $plan.presetId -ne $PresetId.ToString('D') -or ($DisplayId -and $plan.displayId -ne $DisplayId)) {
        throw "The packaged wizard preflight did not confirm the available draft ($($plan.reasonCode))."
    }

    if (!('DccwPlus.PackageRuntimeProcess' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DccwPlus
{
    public static class PackageRuntimeProcess
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetPackageFullName(IntPtr process, ref uint length, StringBuilder name);

        public static bool IsNativeMachine(IntPtr process, ushort expectedMachine)
        {
            ushort processMachine, nativeMachine;
            if (!IsWow64Process2(process, out processMachine, out nativeMachine))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return processMachine == 0 && nativeMachine == expectedMachine;
        }

        public static string ReadPackageFullName(IntPtr process)
        {
            uint length = 0;
            if (GetPackageFullName(process, ref length, null) != 122) return null;
            var name = new StringBuilder(checked((int)length));
            return GetPackageFullName(process, ref length, name) == 0 ? name.ToString() : null;
        }
    }
}
'@ -ErrorAction Stop
    }
    $started = (Get-Date).AddSeconds(-1)
    $opened = (Invoke-DccwAgentCli $Executable $wizardArguments).data
    $gui = Get-Process -Id $opened.processId -ErrorAction Stop
    $owned = $false
    try {
        $owned = $gui.StartTime -ge $started -and
            $gui.MainModule.FileName -eq (Join-Path $InstalledPackage.InstallLocation 'DccwPlus.App.exe') -and
            [DccwPlus.PackageRuntimeProcess]::ReadPackageFullName($gui.Handle) -ceq $InstalledPackage.PackageFullName
        if (!$owned -or ![DccwPlus.PackageRuntimeProcess]::IsNativeMachine($gui.Handle, $expectedMachine)) {
            throw "WinUI activation did not start the installed native $architecture executable."
        }
        if ($opened.status -ne 'opened' -or $opened.changesDisplay -or $opened.presetId -ne $PresetId.ToString('D') -or
            ($DisplayId -and $opened.displayId -ne $DisplayId)) {
            throw 'The CLI did not receive the requested WinUI handoff.'
        }

        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $gui.Refresh()
            if ($gui.HasExited) { throw 'DCCW++ exited before opening its window.' }
            if ($gui.MainWindowHandle -ne 0) { break }
            Start-Sleep -Milliseconds 200
        } while ($timer.Elapsed.TotalSeconds -lt 25)
        if ($gui.MainWindowHandle -eq 0 -or $gui.MainWindowTitle -ne 'DCCW++') {
            throw 'The WinUI main window did not open.'
        }
        $sessionsAfter = @((Invoke-DccwAgentCli $Executable @('calibration', 'sessions')).data |
            ForEach-Object { $_.sessionId } | Sort-Object)
        if (($sessionsBefore -join ',') -ne ($sessionsAfter -join ',')) {
            throw 'The calibration session list changed during the wizard handoff.'
        }

        return [ordered]@{
            wizardHandoff = $true
            wizardDisplayRequested = [bool]$DisplayId
            winUiNativeArchitecture = $architecture
            calibrationSessionCount = $sessionsAfter.Count
            elevated = $elevated
        }
    }
    finally {
        try {
            if ($owned -and !$gui.HasExited -and (!$gui.CloseMainWindow() -or !$gui.WaitForExit(8000)) -and !$gui.HasExited) {
                $gui.Kill()
                $null = $gui.WaitForExit(3000)
            }
        }
        finally { $gui.Dispose() }
    }
}

Export-ModuleMember -Function Invoke-DccwAgentCli, Test-DccwWizardHandoff
