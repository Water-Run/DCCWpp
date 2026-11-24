# Invoked by the sandbox guest runner as a disposable standard user.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = 'C:\DccwPackageSmoke'
$result = [ordered]@{ success = $false; step = 'identity'; standardUser = $false }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$result.standardUser = $identity.Name.Split('\')[-1] -eq 'DccwSmokeUser' -and
    !$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$result.standardUser -or !(Test-Path -LiteralPath (Join-Path $testRoot 'user'))) {
    throw 'Expected the isolated sandbox standard user and its results folder.'
}

function Invoke-PackagedCli([string]$Executable, [string[]]$Command) {
    $response = & $Executable @Command --json | ConvertFrom-Json
    if ($LASTEXITCODE -or !$response.ok) {
        throw ('CLI command failed: ' + ($Command -join ' '))
    }
    return $response.data
}

try {
    $profile = Get-ChildItem -LiteralPath $testRoot -Filter 'DCCWPlus-*.icc' -File | Select-Object -First 1
    if ($profile) {
        $result.step = 'icc_install'
        if ($profile.Name -notmatch '^DCCWPlus-[0-9a-f]{32}\.icc$') { throw 'Expected the generated DCCW++ ICC fixture.' }
        $installedProfile = Join-Path $env:windir "System32\spool\drivers\color\$($profile.Name)"
        if (Test-Path -LiteralPath $installedProfile) { throw 'The disposable ICC name already exists.' }
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class SandboxIccProfile {
    [DllImport("mscms.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
    static extern bool InstallColorProfileW(string machine, string path);
    [DllImport("mscms.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
    static extern bool UninstallColorProfileW(string machine, string name, bool deleteFile);
    public static void Install(string path) {
        if (!InstallColorProfileW(null, path)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static void Uninstall(string name) {
        if (!UninstallColorProfileW(null, name, true)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
'@
        $result.iccInputSha256 = (Get-FileHash -LiteralPath $profile.FullName).Hash
        [SandboxIccProfile]::Install($profile.FullName)
        $result.iccInstalled = $true
        try {
            $result.step = 'icc_verify'
            $result.iccInstalledSha256 = (Get-FileHash -LiteralPath $installedProfile).Hash
            if ($result.iccInstalledSha256 -ne $result.iccInputSha256) { throw 'Installed ICC bytes differ from the supplied file.' }
        }
        finally { [SandboxIccProfile]::Uninstall($profile.Name) }
        $result.step = 'icc_remove'
        if (Test-Path -LiteralPath $installedProfile) { throw 'The disposable ICC remains installed.' }
        $result.iccRemoved = $true
    }

    $result.step = 'package_install'
    Add-AppxPackage -Path (Join-Path $testRoot 'DccwPlus.msix')
    $installed = Get-AppxPackage -Name 'DCCWPlus.Development'
    if (!$installed) { throw 'DCCW++ is not registered for the disposable user.' }
    $result.packageVersion = $installed.Version.ToString()
    $result.packageArchitecture = $installed.Architecture.ToString()
    try {
        $result.step = 'package_alias'
        $alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\dccwpp.exe'
        if (!(Test-Path -LiteralPath $alias)) { throw 'The CLI execution alias was not registered.' }
        $status = Invoke-PackagedCli $alias @('status')
        if (!$status.packaged) { throw 'The alias did not run with package identity.' }
        $result.aliasRanWithPackageIdentity = $true

        $upgradePackage = Join-Path $testRoot 'DccwPlusUpgrade.msix'
        if (Test-Path -LiteralPath $upgradePackage) {
            $result.step = 'upgrade_seed'
            $preset = Invoke-PackagedCli $alias @('presets', 'create', 'Sandbox upgrade fixture', '--gamma', '2.35', '--red-gain', '0.92')
            $observation = Invoke-PackagedCli $alias @('displays', 'snapshot')

            $result.step = 'package_upgrade'
            Add-AppxPackage -Path $upgradePackage
            $upgraded = Get-AppxPackage -Name 'DCCWPlus.Development'
            if (!$upgraded -or $upgraded.PackageFamilyName -ne $installed.PackageFamilyName -or $upgraded.Version -le $installed.Version) {
                throw 'The newer package did not replace the original in the same package family.'
            }
            $result.upgradeVersion = $upgraded.Version.ToString()

            $result.step = 'upgrade_verify'
            $afterStatus = Invoke-PackagedCli $alias @('status')
            if (!$afterStatus.packaged -or $afterStatus.dataDirectory -ne $status.dataDirectory) {
                throw 'The upgraded alias did not run with the original package data directory.'
            }
            $result.upgradeAliasRanWithPackageIdentity = $true
            $afterPreset = Invoke-PackagedCli $alias @('presets', 'show', $preset.id)
            if ($afterPreset.id -ne $preset.id -or $afterPreset.name -cne $preset.name -or
                $afterPreset.calibration.gamma -ne $preset.calibration.gamma -or
                $afterPreset.calibration.redGain -ne $preset.calibration.redGain -or
                $afterPreset.calibration.greenGain -ne $preset.calibration.greenGain -or
                $afterPreset.calibration.blueGain -ne $preset.calibration.blueGain) {
                throw 'The upgrade did not preserve the preset.'
            }
            $result.presetPreserved = $true
            $afterObservation = Invoke-PackagedCli $alias @('displays', 'snapshot', 'show')
            if ($afterObservation.capturedAt -ne $observation.capturedAt -or
                $afterObservation.schemaVersion -ne $observation.schemaVersion) {
                throw 'The upgrade did not preserve the display observation.'
            }
            $result.observationPreserved = $true
        }

        $result.step = 'cli_discovery'
        $capabilities = Invoke-PackagedCli $alias @('capabilities')
        if ($capabilities.contractVersion -ne 1) { throw 'The installed alias did not report the CLI v1 contract.' }
        $result.cliContractVersion = $capabilities.contractVersion
        $result.cliRequestIdCommands = @($capabilities.commands | Where-Object { $_.supportsRequestId } | ForEach-Object { $_.name })
        $result.step = 'cli_redirected_process'
        $processInfo = [Diagnostics.ProcessStartInfo]::new($alias)
        $missingSession = [Guid]::NewGuid().ToString('D')
        $processInfo.Arguments = "calibration status $missingSession --json"
        $processInfo.UseShellExecute = $false
        $processInfo.CreateNoWindow = $true
        $processInfo.RedirectStandardOutput = $true
        $processInfo.RedirectStandardError = $true
        $processInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
        $processInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
        $process = [Diagnostics.Process]::Start($processInfo)
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit(15000)) {
                $process.Kill()
                throw 'The installed CLI did not return to the caller.'
            }
            $response = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json
            if ($process.ExitCode -ne 3 -or $response.ok -or $response.error.code -ne 'not_found' -or $stderr.GetAwaiter().GetResult()) {
                throw 'The installed CLI did not report the JSON error and exit code to the calling process.'
            }
            $result.cliRedirectedProcessExitCode = $process.ExitCode
            $result.cliRedirectedProcessReason = $response.error.code
        }
        finally { $process.Dispose() }
        if ($capabilities.commands.name -contains 'calibration observe') {
            $result.step = 'cli_observe'
            foreach ($group in 'calibration', 'profiles') {
                $command = $capabilities.commands | Where-Object { $_.name -eq "$group observe" }
                $missingId = [Guid]::NewGuid().ToString('D')
                $response = & $alias $group observe $missingId --wait-seconds 1 --json | ConvertFrom-Json
                if ($LASTEXITCODE -ne 3 -or $response.ok -or $response.error.code -ne 'not_found' -or
                    !$command -or $command.effect -ne 'read') {
                    throw "The packaged CLI did not report the expected $group observe contract."
                }
            }
            $result.cliObserveMissingSessions = $true
        }
        $inlinePreview = $capabilities.commands | Where-Object { $_.name -eq 'calibration preview' }
        if ($inlinePreview) {
            $result.step = 'cli_inline_preview'
            $response = & $alias calibration preview --display 'missing-sandbox-display' --gamma 2.3 --dry-run --json | ConvertFrom-Json
            if ($LASTEXITCODE -ne 3 -or $response.ok -or $response.error.code -ne 'not_found' -or
                !$inlinePreview.supportsDryRun -or !$inlinePreview.supportsRequestId) {
                throw 'The packaged CLI did not report the expected inline preview contract.'
            }
            $result.inlinePreviewMissingDisplay = $response.error.code
        }
        if ($capabilities.commands.name -contains 'calibration compare') {
            $result.step = 'cli_capture_comparison'
            $capture = Invoke-PackagedCli $alias @('calibration', 'capture')
            $capturePath = Join-Path $testRoot 'user\capture.json'
            $capture | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $capturePath -Encoding UTF8
            $comparison = Invoke-PackagedCli $alias @('calibration', 'compare', $capturePath, $capturePath)
            if ($comparison.status -notin @('matched', 'inconclusive') -or $comparison.colorAccuracyVerified -or
                $comparison.displays.Count -ne $capture.displays.Count) {
                throw 'The packaged CLI did not compare its own capture correctly.'
            }
            $result.captureComparisonStatus = $comparison.status
            $result.captureComparisonDisplays = $comparison.displays.Count
            if ($capture.PSObject.Properties['activeDisplayCount']) {
                if ($capture.activeDisplayCount -ne $capture.displays.Count -or
                    $comparison.fullDesktopCoverage -ne ($capture.activeDisplayCount -gt 0)) {
                    throw 'The packaged CLI did not report coverage for the full desktop capture.'
                }
                $result.captureActiveDisplayCount = $capture.activeDisplayCount
                $result.captureFullDesktopCoverage = $comparison.fullDesktopCoverage
            }
        }

        $result.step = 'startup_status'
        $startup = & $alias startup status --json | ConvertFrom-Json
        $result.startupExitCode = $LASTEXITCODE
        $result.startup = $startup
        if ($LASTEXITCODE -or !$startup.ok -or $startup.data.isEnabled -or !$startup.data.canEnable -or
            $startup.data.state -ne 'Disabled') {
            throw 'The packaged startup task did not report the clean-install default of Disabled.'
        }
        $result.step = 'package_remove'
    }
    finally {
        foreach ($registered in Get-AppxPackage -Name 'DCCWPlus.Development') {
            Remove-AppxPackage -Package $registered.PackageFullName
        }
        $result.packageRemoved = !(Get-AppxPackage -Name 'DCCWPlus.Development')
    }
    if (!$result.packageRemoved) { throw 'DCCW++ remains registered after package removal.' }
    $result.step = 'complete'
    $result.success = $true
}
catch {
    $errorRecord = $_.Exception.GetBaseException()
    $result.error = $errorRecord.Message
    if ($errorRecord -is [ComponentModel.Win32Exception]) { $result.win32 = $errorRecord.NativeErrorCode }
}
finally {
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'user\result.json') -Encoding UTF8
}
