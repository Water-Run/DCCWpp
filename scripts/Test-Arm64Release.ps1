[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or
    $env:RUNNER_OS -ne 'Windows' -or $env:RUNNER_ARCH -ne 'ARM64' -or
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'Arm64') {
    throw 'This installation check requires an isolated GitHub-hosted Windows ARM64 runner.'
}

$reportFile = [IO.Path]::GetFullPath($ReportPath)
$null = New-Item -Path (Split-Path -Parent $reportFile) -ItemType Directory -Force
$report = [ordered]@{
    schemaVersion = 1
    success = $false
    step = 'verify_evidence'
    sourceCommit = $env:GITHUB_SHA
    osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    windowsBuild = [Environment]::OSVersion.Version.Build
}
$name = 'DCCWPlus.Development'
$publisher = 'CN=DCCWPlus-Development'
$scratch = Join-Path $env:RUNNER_TEMP ('dccwpp-' + [Guid]::NewGuid().ToString('N'))
$cliData = Join-Path $scratch 'agent-data'
$certificate = $null
$attemptedInstall = $false

function Invoke-AgentCli([string]$Executable, [string[]]$Command, [int]$ExpectedExitCode = 0, [switch]$UseDefaultData) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $arguments = $Command + @('--json')
    if (!$UseDefaultData) { $arguments += @('--data-dir', $cliData) }
    foreach ($argument in $arguments) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(25000)) {
            $process.Kill()
            throw "CLI request timed out: $($Command -join ' ')"
        }
        $response = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json
        if ($process.ExitCode -ne $ExpectedExitCode -or $stderr.GetAwaiter().GetResult() -or
            $response.schemaVersion -ne 1 -or $response.ok -ne ($ExpectedExitCode -eq 0)) {
            throw "Unexpected CLI result for $($Command -join ' '): $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())"
        }
        return $response
    }
    finally { $process.Dispose() }
}

try {
    $evidence = Get-Content -LiteralPath (Join-Path $EvidenceDirectory 'evidence.json') -Raw | ConvertFrom-Json
    $packages = @($evidence.packages | Where-Object architecture -EQ 'arm64')
    if ($evidence.schemaVersion -ne 1 -or $evidence.channel -ne 'development' -or
        $evidence.builtFrom -ne 'git-commit' -or $evidence.sourceCommit -cne $env:GITHUB_SHA -or $packages.Count -ne 1) {
        throw 'Expected the ARM64 development package for this workflow commit.'
    }
    $package = $packages[0]
    if ($package.identity -ne $name -or $package.publisher -cne $publisher -or
        $package.file -cnotmatch '^packages/[A-Za-z0-9._-]+_arm64\.msix$') {
        throw 'Unexpected ARM64 development package identity or path.'
    }
    $unsigned = Join-Path $EvidenceDirectory $package.file
    $report.unsignedSha256 = (Get-FileHash -LiteralPath $unsigned -Algorithm SHA256).Hash
    if ($report.unsignedSha256 -ne $package.sha256) { throw 'MSIX digest differs from the build evidence.' }

    $report.step = 'sign_and_install'
    Import-Module Appx -UseWindowsPowerShell
    if (Get-AppxPackage -Name $name) { throw 'Expected a clean development package identity.' }
    $null = New-Item -Path $scratch -ItemType Directory
    $signed = Join-Path $scratch 'DccwPlus-arm64.msix'
    Copy-Item -LiteralPath $unsigned -Destination $signed
    $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $signTool = Get-ChildItem -Path (Join-Path $sdkBin '10.*/arm64/signtool.exe') -File -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
    if (!$signTool) { throw 'Windows SDK ARM64 SignTool was not found.' }
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $publisher `
        -CertStoreLocation 'Cert:\LocalMachine\My' -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddDays(1)
    $cerFile = Join-Path $scratch 'development.cer'
    Export-Certificate -Cert $certificate -FilePath $cerFile | Out-Null
    Import-Certificate -FilePath $cerFile -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    & $signTool.FullName sign /sm /sha1 $certificate.Thumbprint /fd SHA256 $signed
    if ($LASTEXITCODE) { throw 'SignTool could not sign the isolated MSIX copy.' }
    $attemptedInstall = $true
    Add-AppxPackage -Path $signed
    $installed = Get-AppxPackage -Name $name
    if (!$installed -or $installed.Architecture.ToString() -ne 'Arm64' -or $installed.Version.ToString() -ne $package.version) {
        throw 'Windows did not register the expected ARM64 package version.'
    }
    $report.packageVersion = $package.version
    $report.packageArchitecture = $installed.Architecture.ToString()

    $report.step = 'cli_alias'
    $alias = Join-Path $env:LOCALAPPDATA 'Microsoft/WindowsApps/dccwpp.exe'
    $status = (Invoke-AgentCli $alias @('status')).data
    if (!$status.packaged -or $status.dataDirectory -ne $cliData) { throw 'The CLI alias did not run inside the installed package.' }
    $report.cliAliasPackaged = $true
    $capabilities = (Invoke-AgentCli $alias @('capabilities')).data
    if ($capabilities.contractVersion -ne 1) { throw 'Unexpected Agent CLI contract version.' }
    $report.cliContractVersion = $capabilities.contractVersion
    $missing = Invoke-AgentCli $alias @('calibration', 'status', [Guid]::NewGuid().ToString('D')) 3
    if ($missing.error.code -ne 'not_found') { throw 'The Agent CLI did not return the expected missing-session reason.' }
    $report.cliMissingSessionExitCode = 3
    $startup = (Invoke-AgentCli $alias @('startup', 'status')).data
    if ($startup.isEnabled -or $startup.state -ne 'Disabled') { throw 'The clean MSIX did not report disabled sign-in startup.' }
    $report.startupState = $startup.state

    $report.step = 'cli_smoke'
    $smoke = & python (Join-Path $PSScriptRoot 'smoke_cli.py') $alias
    $smoke | Write-Host
    if ($LASTEXITCODE -or ($smoke -join "`n") -notmatch 'PASS: (\d+) CLI process checks') { throw 'The ARM64 CLI smoke suite failed.' }
    $report.cliProcessChecks = [int]$Matches[1]

    $report.step = 'wizard_handoff'
    $report.runnerInteractive = [Environment]::UserInteractive
    $preset = (Invoke-AgentCli $alias @('presets', 'create', 'ARM64 review', '--gamma', '2.4') -UseDefaultData).data
    $wizardArguments = @('wizard', 'open', '--preset', $preset.id)
    $display = (Invoke-AgentCli $alias @('displays', 'list')).data |
        Where-Object { $_.deviceName.StartsWith('\\.\DISPLAY', [StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object isPrimary | Select-Object -First 1
    if ($display) { $wizardArguments += @('--display', $display.id) }
    $plan = (Invoke-AgentCli $alias ($wizardArguments + '--dry-run') -UseDefaultData).data
    if (!$plan.canOpen -or $plan.reasonCode -ne 'wizard_ready' -or $plan.changesDisplay) {
        throw 'The packaged wizard preflight did not confirm the available draft.'
    }
    $opened = (Invoke-AgentCli $alias $wizardArguments -UseDefaultData).data
    if ($opened.status -ne 'opened' -or $opened.changesDisplay -or $opened.presetId -ne $preset.id -or
        ($display -and $opened.displayId -ne $display.id)) {
        throw 'The CLI did not receive the requested WinUI handoff.'
    }
    $report.wizardHandoff = $true
    $report.wizardDisplayRequested = [bool]$display
    $sessions = (Invoke-AgentCli $alias @('calibration', 'sessions') -UseDefaultData).data
    if ($sessions) { throw 'The wizard handoff started a calibration session.' }

    $report.step = 'winui_launch'
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class Arm64ReleaseProcess {
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int GetPackageFullName(IntPtr process, ref uint length, StringBuilder name);

    public static bool IsNativeArm64(IntPtr process) {
        ushort processMachine, nativeMachine;
        if (!IsWow64Process2(process, out processMachine, out nativeMachine))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return processMachine == 0 && nativeMachine == 0xaa64;
    }

    public static string ReadPackageFullName(IntPtr process) {
        uint length = 0;
        if (GetPackageFullName(process, ref length, null) != 122) return null;
        var name = new StringBuilder(checked((int)length));
        return GetPackageFullName(process, ref length, name) == 0 ? name.ToString() : null;
    }
}
'@
    $gui = Get-Process -Id $opened.processId
    try {
        if ($gui.MainModule.FileName -ne (Join-Path $installed.InstallLocation 'DccwPlus.App.exe') -or
            ![Arm64ReleaseProcess]::IsNativeArm64($gui.Handle) -or
            [Arm64ReleaseProcess]::ReadPackageFullName($gui.Handle) -cne $installed.PackageFullName) {
            throw 'WinUI activation did not start the installed native ARM64 executable.'
        }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 200
            $gui.Refresh()
            if ($gui.HasExited) { throw 'DCCW++ exited before opening its window.' }
        } until ($gui.MainWindowHandle -ne 0 -or $timer.Elapsed.TotalSeconds -ge 25)
        if ($gui.MainWindowHandle -eq 0 -or $gui.MainWindowTitle -ne 'DCCW++') { throw 'The WinUI main window did not open.' }
        $report.winUiNativeArm64 = $true
    }
    finally {
        if (!$gui.HasExited -and $gui.MainModule.FileName -eq (Join-Path $installed.InstallLocation 'DccwPlus.App.exe')) {
            if (!$gui.CloseMainWindow() -or !$gui.WaitForExit(8000)) { $gui.Kill(); $gui.WaitForExit() }
        }
        $gui.Dispose()
    }

    $report.step = 'remove_package'
    Remove-AppxPackage -Package $installed.PackageFullName
    $report.packageRemoved = !(Get-AppxPackage -Name $name)
    if (!$report.packageRemoved) { throw 'The development package is still registered.' }
    $report.step = 'complete'
    $report.success = $true
}
catch {
    $report.error = $_.Exception.GetBaseException().Message
    throw
}
finally {
    try {
        if ($attemptedInstall -and !$report.Contains('packageRemoved')) {
            Get-AppxPackage -Name $name | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName }
        }
        if ($certificate) {
            Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath "Cert:\LocalMachine\My\$($certificate.Thumbprint)" -DeleteKey
        }
    }
    finally { $report | ConvertTo-Json | Set-Content -LiteralPath $reportFile -Encoding utf8 }
}
