#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppAssets,
    [Parameter(Mandatory)][string]$CliDependencies,
    [Parameter(Mandatory)][string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$assets = Get-Content -LiteralPath $AppAssets -Raw | ConvertFrom-Json -AsHashtable
$cli = Get-Content -LiteralPath $CliDependencies -Raw | ConvertFrom-Json -AsHashtable

# WinAppSDK targets also copy native redistributables not listed in the GUI's .deps.json.
$included = @($assets.libraries.Keys | Where-Object {
    $assets.libraries[$_].type -eq 'package' -and $_ -notlike 'Microsoft.Windows.SDK.BuildTools*/*'
})
$included += @($cli.libraries.Keys | Where-Object { $cli.libraries[$_].type -eq 'runtimepack' } |
    ForEach-Object { $_ -replace '^runtimepack\.', '' })

function Find-PackageDirectory([string]$Package) {
    foreach ($folder in $assets.packageFolders.Keys) {
        $candidate = Join-Path $folder $Package.ToLowerInvariant()
        if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
    }
    throw "Resolved NuGet package $Package is missing from the restored cache."
}

$noticePattern = '(?i)^(license|notice|third[-_. ]?party[-_. ]?notices?|sdk_license)(\.(txt|md|rtf))?$'
$runtime = $included | Where-Object { $_ -like 'Microsoft.NETCore.App.Runtime.*/*' } | Select-Object -First 1
if (!$runtime) { throw 'The published CLI is missing its .NET runtime pack.' }
$dotnetLicense = Join-Path (Find-PackageDirectory $runtime) 'LICENSE.TXT'

$records = @(
    foreach ($package in ($included | Sort-Object -Unique)) {
        $id, $version = $package.Split('/')
        $folder = Find-PackageDirectory $package
        $manifestPath = Get-ChildItem -LiteralPath $folder -Filter '*.nuspec' -File | Select-Object -First 1
        if (!$manifestPath) { throw "NuGet package $package has no metadata." }
        [xml]$manifest = Get-Content -LiteralPath $manifestPath.FullName -Raw
        $license = $manifest.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
        $licenseUrl = $manifest.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='licenseUrl']")
        $expression = if ($license -and $license.GetAttribute('type') -eq 'expression') { $license.InnerText } else { $null }
        $files = @(Get-ChildItem -LiteralPath $folder -File | Where-Object Name -Match $noticePattern | Sort-Object Name)
        if ($id -eq 'Microsoft.Win32.SystemEvents' -and $expression -eq 'MIT' -and
            !($files | Where-Object Name -Match '^LICENSE')) {
            # SystemEvents is distributed from dotnet/runtime under the same MIT license.
            $files += Get-Item -LiteralPath $dotnetLicense
        }
        if ($files.Count -eq 0 -and !$licenseUrl) { throw "No redistribution notice or license reference was found for $package." }
        $documents = @(
            foreach ($file in $files) {
                $relativePath = "$($package.ToLowerInvariant())/$($file.Name)"
                $destination = Join-Path $OutputDirectory $relativePath
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                Copy-Item -LiteralPath $file.FullName -Destination $destination
                [ordered]@{ path = $relativePath; sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash }
            }
        )
        [ordered]@{ id = $id; version = $version; licenseExpression = $expression
            licenseUrl = if ($licenseUrl) { $licenseUrl.InnerText } else { $null }; documents = $documents }
    }
)
[ordered]@{ schemaVersion = 1; packages = $records } | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'index.json') -Encoding utf8
