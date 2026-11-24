#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [ValidateRange(45, 600)][int]$TimeoutSeconds = 240
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$packagePath = (Resolve-Path -LiteralPath $Package).Path
$wsb = (Get-Command wsb.exe -ErrorAction Stop).Source
$sdkTools = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64'
$signTool = Join-Path $sdkTools 'signtool.exe'
if (!(Test-Path -LiteralPath $signTool)) { throw 'Install Windows SDK 26100 build tools before running this check.' }

$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
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
}
finally { $archive.Dispose() }

$sandboxId = [Guid]::NewGuid().ToString('D')
$run = Join-Path $workspace "artifacts\sandbox-smoke\$sandboxId"
$inputDirectory = Join-Path $run 'input'
$resultDirectory = Join-Path $run 'results'
New-Item -ItemType Directory -Path $inputDirectory, $resultDirectory | Out-Null
Copy-Item -LiteralPath $packagePath -Destination (Join-Path $inputDirectory 'DccwPlus.msix')
Copy-Item -LiteralPath $signTool, (Join-Path $PSScriptRoot 'SandboxPackageGuest.ps1') -Destination $inputDirectory
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
try {
    & $wsb start --id $sandboxId --config $config --raw
    if ($LASTEXITCODE -ne 0) { throw 'Windows Sandbox could not start.' }
    $guestCommand = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\DccwInput\SandboxPackageGuest.ps1'
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
    if ($result.inputSha256 -ne (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash) {
        throw "Sandbox tested a different package. Report: $resultPath"
    }
}
finally {
    & $wsb stop --id $sandboxId --raw | Out-Null
    if ($guest) { $guest.Dispose() }
}
