#Requires -Version 7.0
<#
.SYNOPSIS
Build DCCW++, run core and simulated worker tests, or generate an unsigned MSIX package.
.EXAMPLE
./scripts/Build.ps1 -Test -Package
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [switch]$Test,
    [switch]$Package,
    [switch]$Store,
    [string]$StoreIdentityFile,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$')][string]$PackageRun
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
$identityFile = Join-Path $taskRoot 'store/identity.local.json'
if ($Store -and $StoreIdentityFile) {
    $identityFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($StoreIdentityFile)
}
Push-Location -LiteralPath $taskRoot
try {
    if ($Store -and -not $Package) { throw '-Store requires -Package.' }
    if ($PackageRun -and -not $Package) { throw '-PackageRun requires -Package.' }
    if ($Store) {
        if (-not (Test-Path -LiteralPath $identityFile -PathType Leaf)) {
            throw 'Create the local identity file using the DCCW++ values from Partner Center. See docs/STORE.md.'
        }
        & python ./scripts/verify_package.py --manifest-only src/DccwPlus.App/Package.appxmanifest --store-identity $identityFile
        if ($LASTEXITCODE -ne 0) { throw 'Store manifest verification failed. See docs/STORE.md.' }
    }
    if ($Test) {
        & dotnet test 'tests\DccwPlus.Core.Tests\DccwPlus.Core.Tests.csproj' -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
        & dotnet test 'tests\DccwPlus.Windows.Tests\DccwPlus.Windows.Tests.csproj' -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Windows worker tests failed.' }
    }
    $buildArguments = @('src\DccwPlus.App\DccwPlus.App.csproj', '/restore', '/m', '/nologo', '/verbosity:minimal', "/p:Configuration=$Configuration", "/p:Platform=$Architecture", "/p:RuntimeIdentifier=$runtime")
    & dotnet publish 'src\DccwPlus.Cli\DccwPlus.Cli.csproj' -c $Configuration -r $runtime --self-contained true -o $cliOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
    $buildArguments += "/p:CliPublishDirectory=$cliOutput"
    if ($Package) {
        $restoreArgs = @('src\DccwPlus.App\DccwPlus.App.csproj', '/t:Restore', '/nologo', '/verbosity:minimal',
            "/p:Configuration=$Configuration", "/p:Platform=$Architecture", "/p:RuntimeIdentifier=$runtime")
        & $msbuildPath @restoreArgs
        if ($LASTEXITCODE -ne 0) { throw 'WinUI dependency restore failed.' }
        $noticeOutput = Join-Path $taskRoot "artifacts\notices\$runtime\$([Guid]::NewGuid().ToString('N'))"
        & "$PSScriptRoot/Stage-ThirdPartyNotices.ps1" -AppAssets 'src/DccwPlus.App/obj/project.assets.json' -CliDependencies (Join-Path $cliOutput 'dccwpp.deps.json') -OutputDirectory $noticeOutput
        $buildArguments += "/p:ThirdPartyNoticeDirectory=$noticeOutput"
        $packageOutput = Join-Path $taskRoot 'artifacts\packages'
        if ($PackageRun) { $packageOutput = Join-Path $packageOutput $PackageRun }
        $packageOutput += [IO.Path]::DirectorySeparatorChar
        $mode = if ($Store) { 'StoreUpload' } else { 'SideloadOnly' }
        $buildArguments += @('/p:GenerateAppxPackageOnBuild=true', '/p:AppxPackageSigningEnabled=false', "/p:AppxPackageDir=$packageOutput", "/p:UapAppxPackageBuildMode=$mode")
    }
    else {
        $buildArguments += '/p:WindowsPackageType=None'
    }
    & $msbuildPath @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }
}
finally { Pop-Location }
