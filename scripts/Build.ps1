#Requires -Version 7.0
<#
.SYNOPSIS
Build DCCW++, run core tests, or generate an unsigned MSIX package.
.EXAMPLE
./scripts/Build.ps1 -Test -Package
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [switch]$Test,
    [switch]$Package,
    [switch]$Store
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio with the Windows application development workload is required.' }
$msbuildPath = & $vswhere -latest -prerelease -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuildPath) { throw 'MSBuild was not found.' }
$runtime = 'win-' + $Architecture.ToLowerInvariant()
$cliOutput = Join-Path $taskRoot "artifacts\cli\$runtime"
Push-Location -LiteralPath $taskRoot
try {
    if ($Store -and -not $Package) { throw '-Store requires -Package.' }
    if ($Store) {
        [xml]$manifest = Get-Content -LiteralPath 'src\DccwPlus.App\Package.appxmanifest' -Raw
        if ($manifest.Package.Identity.Name -eq 'DCCWPlus.Development' -or $manifest.Package.Identity.Publisher -eq 'CN=DCCWPlus-Development') {
            throw 'Associate DCCW++ with its own Partner Center identity before generating a Store submission. See docs/STORE.md.'
        }
    }
    if ($Test) {
        & dotnet test 'tests\DccwPlus.Core.Tests\DccwPlus.Core.Tests.csproj' -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    }
    $buildArguments = @('src\DccwPlus.App\DccwPlus.App.csproj', '/restore', '/m', '/nologo', '/verbosity:minimal', "/p:Configuration=$Configuration", "/p:Platform=$Architecture", "/p:RuntimeIdentifier=$runtime")
    if ($Package) {
        & dotnet publish 'src\DccwPlus.Cli\DccwPlus.Cli.csproj' -c $Configuration -r $runtime --self-contained true -o $cliOutput --nologo
        if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
        $packageOutput = Join-Path $taskRoot 'artifacts\packages\'
        $mode = if ($Store) { 'StoreUpload' } else { 'SideloadOnly' }
        $buildArguments += @('/p:GenerateAppxPackageOnBuild=true', '/p:AppxPackageSigningEnabled=false', "/p:CliPublishDirectory=$cliOutput", "/p:AppxPackageDir=$packageOutput", "/p:UapAppxPackageBuildMode=$mode")
    }
    else {
        $buildArguments += '/p:WindowsPackageType=None'
        & dotnet build 'src\DccwPlus.Cli\DccwPlus.Cli.csproj' -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw 'CLI build failed.' }
    }
    & $msbuildPath @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }
}
finally { Pop-Location }
