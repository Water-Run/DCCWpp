[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Architecture,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][string]$ReportPath,
    [string]$WackReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$architectureName = $Architecture.ToLowerInvariant()
$osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or
    $env:RUNNER_OS -ne 'Windows' -or $env:RUNNER_ARCH -ne $architectureName -or $osArchitecture -ne $architectureName) {
    throw "This installation check requires an isolated GitHub-hosted Windows $architectureName runner."
}
if ($WackReportPath -and $architectureName -ne 'x64') { throw 'The hosted WACK preflight runs with the x64 development package.' }

$reportFile = [IO.Path]::GetFullPath($ReportPath)
$null = New-Item -Path (Split-Path -Parent $reportFile) -ItemType Directory -Force
$report = [ordered]@{
    schemaVersion = 1
    success = $false
    step = 'verify_evidence'
    sourceCommit = $env:GITHUB_SHA
    osArchitecture = $osArchitecture
    windowsBuild = [Environment]::OSVersion.Version.Build
    windowsInstallationType = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name InstallationType
    runnerInteractive = [Environment]::UserInteractive
}
$name = 'DCCWPlus.Development'
$publisher = 'CN=DCCWPlus-Development'
$scratch = Join-Path $env:RUNNER_TEMP ('dccwpp-' + [Guid]::NewGuid().ToString('N'))
$cliData = Join-Path $scratch 'agent-data'
$certificate = $null
$attemptedInstall = $false

Import-Module (Join-Path $PSScriptRoot 'PackageRuntimeChecks.psm1')

try {
    $user = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    $report.runnerElevated = $user.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $evidence = Get-Content -LiteralPath (Join-Path $EvidenceDirectory 'evidence.json') -Raw | ConvertFrom-Json
    $packages = @($evidence.packages | Where-Object architecture -CEQ $architectureName)
    if ($evidence.schemaVersion -ne 1 -or $evidence.channel -ne 'development' -or
        $evidence.builtFrom -ne 'git-commit' -or $evidence.sourceCommit -cne $env:GITHUB_SHA -or $packages.Count -ne 1) {
        throw "Expected the $architectureName development package for this workflow commit."
    }
    $package = $packages[0]
    if ($package.identity -ne $name -or $package.publisher -cne $publisher -or
        $package.file -cnotmatch "^packages/[A-Za-z0-9._-]+_${architectureName}\.msix$") {
        throw "Unexpected $architectureName development package identity or path."
    }
    $unsigned = Join-Path $EvidenceDirectory $package.file
    $report.unsignedSha256 = (Get-FileHash -LiteralPath $unsigned -Algorithm SHA256).Hash
    if ($report.unsignedSha256 -ne $package.sha256) { throw 'MSIX digest differs from the build evidence.' }

    $report.step = 'sign_and_install'
    Import-Module Appx -UseWindowsPowerShell
    if (Get-AppxPackage -Name $name) { throw 'Expected a clean development package identity.' }
    $null = New-Item -Path $scratch -ItemType Directory
    $signed = Join-Path $scratch "DccwPlus-$architectureName.msix"
    Copy-Item -LiteralPath $unsigned -Destination $signed
    $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $signTool = Get-ChildItem -Path (Join-Path $sdkBin "10.*/$architectureName/signtool.exe") -File -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
    if (!$signTool) { throw "Windows SDK $architectureName SignTool was not found." }
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
    if (!$installed -or $installed.Architecture.ToString() -ne $architectureName -or $installed.Version.ToString() -ne $package.version) {
        throw "Windows did not register the expected $architectureName package version."
    }
    $report.packageVersion = $package.version
    $report.packageArchitecture = $installed.Architecture.ToString()

    $report.step = 'cli_alias'
    $alias = Join-Path $env:LOCALAPPDATA 'Microsoft/WindowsApps/dccwpp.exe'
    $status = (Invoke-DccwAgentCli $alias @('status') -DataDirectory $cliData).data
    if (!$status.packaged -or $status.dataDirectory -ne $cliData) { throw 'The CLI alias did not run inside the installed package.' }
    $report.cliAliasPackaged = $true
    $capabilities = (Invoke-DccwAgentCli $alias @('capabilities') -DataDirectory $cliData).data
    if ($capabilities.contractVersion -ne 1) { throw 'Unexpected Agent CLI contract version.' }
    $report.cliContractVersion = $capabilities.contractVersion
    $missing = Invoke-DccwAgentCli $alias @('calibration', 'status', [Guid]::NewGuid().ToString('D')) -DataDirectory $cliData -ExpectedExitCode 3
    if ($missing.error.code -ne 'not_found') { throw 'The Agent CLI did not return the expected missing-session reason.' }
    $report.cliMissingSessionExitCode = 3
    $startup = (Invoke-DccwAgentCli $alias @('startup', 'status') -DataDirectory $cliData).data
    if ($startup.isEnabled -or $startup.state -ne 'Disabled') { throw 'The clean MSIX did not report disabled sign-in startup.' }
    $report.startupState = $startup.state

    $report.step = 'cli_smoke'
    $smoke = & python (Join-Path $PSScriptRoot 'smoke_cli.py') $alias
    $smoke | Write-Host
    if ($LASTEXITCODE -or ($smoke -join "`n") -notmatch 'PASS: (\d+) CLI process checks') { throw "The $architectureName CLI smoke suite failed." }
    $report.cliProcessChecks = [int]$Matches[1]

    $report.step = 'wizard_handoff'
    $preset = (Invoke-DccwAgentCli $alias @('presets', 'create', 'Packaged review', '--gamma', '2.4')).data
    $handoff = Test-DccwWizardHandoff -InstalledPackage $installed -Executable $alias -PresetId $preset.id
    if ($handoff.calibrationSessionCount -ne 0) { throw 'The clean package had a calibration session during wizard handoff.' }
    foreach ($field in 'wizardHandoff', 'wizardDisplayRequested', 'winUiNativeArchitecture', 'calibrationSessionCount') {
        $report[$field] = $handoff[$field]
    }

    $report.step = 'remove_package'
    Remove-AppxPackage -Package $installed.PackageFullName
    $report.packageRemoved = !(Get-AppxPackage -Name $name)
    if (!$report.packageRemoved) { throw 'The development package is still registered.' }
    if ($WackReportPath) {
        $report.step = 'wack_preflight'
        & (Join-Path $PSScriptRoot 'Test-WackPreflight.ps1') -SignedMsix $signed -ReportPath $WackReportPath
        $report.wackPreflightPassed = $true
    }
    $report.step = 'complete'
    $report.success = $true
}
catch {
    $report.error = $_.Exception.GetBaseException().Message
    throw
}
finally {
    try {
        if ($attemptedInstall) {
            Get-AppxPackage -Name $name | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName }
        }
        if ($certificate) {
            Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath "Cert:\LocalMachine\My\$($certificate.Thumbprint)" -DeleteKey
        }
    }
    finally { $report | ConvertTo-Json | Set-Content -LiteralPath $reportFile -Encoding utf8 }
}
