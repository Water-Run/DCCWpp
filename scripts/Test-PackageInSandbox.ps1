#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [switch]$StandardUser,
    [string]$UpgradePackage,
    [string]$IccProfile,
    [ValidateRange(45, 600)][int]$TimeoutSeconds = 240
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$packagePath = (Resolve-Path -LiteralPath $Package).Path
$upgradePath = $null
if ($UpgradePackage) {
    if (!$StandardUser) { throw '-UpgradePackage requires -StandardUser.' }
    $upgradePath = (Resolve-Path -LiteralPath $UpgradePackage).Path
}
$profile = $null
if ($IccProfile) {
    if (!$StandardUser) { throw '-IccProfile requires -StandardUser.' }
    $profile = Get-Item -LiteralPath $IccProfile
    if ($profile -isnot [IO.FileInfo] -or $profile.Name -notmatch '^DCCWPlus-[0-9a-f]{32}\.icc$' -or
        $profile.Length -lt 132 -or $profile.Length -gt 8MB) {
        throw 'Pass a DCCW++ ICC test fixture generated with New-IccTestFixture.cs.'
    }
}
$wsb = (Get-Command wsb.exe -ErrorAction Stop).Source
$sdkTools = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64'
$signTool = Join-Path $sdkTools 'signtool.exe'
if (!(Test-Path -LiteralPath $signTool)) { throw 'Install Windows SDK 26100 build tools before running this check.' }

function Read-DevelopmentVersion([string]$Path) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        if ($null -ne $archive.GetEntry('AppxSignature.p7x')) { throw 'Pass an unsigned development package.' }
        $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
        try { [xml]$manifest = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $identity = $manifest.Package.Identity
        if ($identity.Name -ne 'DCCWPlus.Development' -or $identity.Publisher -ne 'CN=DCCWPlus-Development' -or
            $identity.ProcessorArchitecture -ne 'x64') {
            throw 'This check expects the x64 development identity.'
        }
        return [version]$identity.Version
    }
    finally { $archive.Dispose() }
}

function Assert-SandboxAvailable {
    $running = & $wsb list --raw
    if ($LASTEXITCODE -ne 0) { throw 'Windows Sandbox could not report running instances.' }
    $status = $running | ConvertFrom-Json
    if (@($status.WindowsSandboxEnvironments).Count -gt 0) {
        throw 'A Windows Sandbox instance is already running. The package check left it undisturbed.'
    }
}

function Start-PackageSandbox([string]$Id, [string]$Configuration) {
    $gate = [Threading.Mutex]::new($false, 'Local\DCCWPlus.PackageSandboxStart')
    $acquired = $false
    try {
        try { $acquired = $gate.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (!$acquired) { throw 'Another package check is starting Windows Sandbox.' }
        Assert-SandboxAvailable
        & $wsb start --id $Id --config $Configuration --raw
        if ($LASTEXITCODE -ne 0) { throw 'Windows Sandbox could not start.' }
    }
    finally {
        if ($acquired) { $gate.ReleaseMutex() }
        $gate.Dispose()
    }
}

$packageVersion = Read-DevelopmentVersion $packagePath
$upgradeVersion = $null
if ($upgradePath) {
    $upgradeVersion = Read-DevelopmentVersion $upgradePath
    if ($upgradeVersion -le $packageVersion) { throw 'The upgrade package must have a higher version.' }
}

Assert-SandboxAvailable
$sandboxId = [Guid]::NewGuid().ToString('D')
$run = Join-Path $workspace "artifacts\sandbox-smoke\$sandboxId"
$inputDirectory = Join-Path $run 'input'
$resultDirectory = Join-Path $run 'results'
New-Item -ItemType Directory -Path $inputDirectory, $resultDirectory | Out-Null
Copy-Item -LiteralPath $packagePath -Destination (Join-Path $inputDirectory 'DccwPlus.msix')
if ($upgradePath) { Copy-Item -LiteralPath $upgradePath -Destination (Join-Path $inputDirectory 'DccwPlusUpgrade.msix') }
Copy-Item -LiteralPath $signTool, (Join-Path $PSScriptRoot 'SandboxPackageGuest.ps1') -Destination $inputDirectory
if ($StandardUser) {
    foreach ($file in 'SandboxUserProcess.cs', 'SandboxStandardUserGuest.ps1') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $inputDirectory
    }
    if ($profile) { Copy-Item -LiteralPath $profile.FullName -Destination $inputDirectory }
}
$inputXml = [Security.SecurityElement]::Escape($inputDirectory)
$outputXml = [Security.SecurityElement]::Escape($resultDirectory)
$config = @"
<Configuration>
  <VGpu>Disable</VGpu>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <PrinterRedirection>Disable</PrinterRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <MappedFolders>
    <MappedFolder><HostFolder>$inputXml</HostFolder><SandboxFolder>C:\DccwInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$outputXml</HostFolder><SandboxFolder>C:\DccwResult</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
</Configuration>
"@
$resultPath = Join-Path $resultDirectory 'result.json'
Write-Output "Sandbox run: $sandboxId"
$guest = $null
$started = $false
try {
    Start-PackageSandbox $sandboxId $config
    $started = $true
    $guestCommand = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\DccwInput\SandboxPackageGuest.ps1'
    if ($StandardUser) { $guestCommand += ' -StandardUser' }
    $processInfo = [Diagnostics.ProcessStartInfo]::new($wsb)
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    foreach ($argument in @('exec', '--id', $sandboxId, '--run-as', 'System', '--command', $guestCommand, '--raw')) {
        $processInfo.ArgumentList.Add($argument)
    }
    $guest = [Diagnostics.Process]::Start($processInfo)
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath $resultPath) -and !$guest.HasExited -and
        $deadline.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        Start-Sleep -Seconds 1
    }
    if (!(Test-Path -LiteralPath $resultPath)) {
        throw "Sandbox did not produce a complete report. Expected $resultPath"
    }
    $rawResult = Get-Content -LiteralPath $resultPath -Raw
    $result = $rawResult | ConvertFrom-Json
    Write-Output $rawResult
    if (!$result.success) { throw "Package check stopped at $($result.step). Report: $resultPath" }
    $expectedScope = if ($StandardUser) { 'standard-user' } else { 'provisioning' }
    if ($result.scope -ne $expectedScope -or ($StandardUser -and !$result.userCheck.standardUser)) {
        throw "Sandbox ran a different package check. Report: $resultPath"
    }
    if ($result.inputSha256 -ne (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash) {
        throw "Sandbox tested a different package. Report: $resultPath"
    }
    if ($StandardUser -and [version]$result.userCheck.packageVersion -ne $packageVersion) {
        throw "Sandbox installed a different version. Report: $resultPath"
    }
    if ($upgradePath -and ([version]$result.userCheck.upgradeVersion -ne $upgradeVersion -or
        $result.upgradeInputSha256 -ne (Get-FileHash -LiteralPath $upgradePath -Algorithm SHA256).Hash)) {
        throw "Sandbox tested a different upgrade package. Report: $resultPath"
    }
    if ($profile -and $result.userCheck.iccInputSha256 -ne (Get-FileHash -LiteralPath $profile.FullName).Hash) {
        throw "Sandbox tested a different ICC. Report: $resultPath"
    }
}
finally {
    if ($started) { & $wsb stop --id $sandboxId --raw | Out-Null }
    if ($guest) { $guest.Dispose() }
}
