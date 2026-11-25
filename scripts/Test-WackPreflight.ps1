#Requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Run')][string]$SignedMsix,
    [Parameter(Mandatory, ParameterSetName = 'Read')][switch]$ReadExistingReport,
    [Parameter(Mandatory)][string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-WackReport([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 64MB
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally { $reader.Dispose() }
    $root = $document.DocumentElement
    if ($null -eq $root -or $root.Name -cne 'REPORT' -or $root.GetAttribute('APP_TYPE') -cne 'Centennial') {
        throw 'Expected a Desktop Bridge Windows App Certification Kit report.'
    }
    $partialRun = $root.GetAttribute('PARTIAL_RUN')
    $overall = $root.GetAttribute('OVERALL_RESULT')
    if ($partialRun -cnotin @('TRUE', 'FALSE') -or !$overall) { throw 'The WACK report has no completed run state.' }
    $testNodes = $root.SelectNodes('REQUIREMENTS/REQUIREMENT/TEST')
    if (!$testNodes.Count) { throw 'The WACK report contains no tests.' }
    $tests = foreach ($test in $testNodes) {
        $optional = $test.GetAttribute('OPTIONAL')
        $elements = $test.SelectNodes('RESULT')
        if ($optional -cnotin @('TRUE', 'FALSE') -or $elements.Count -ne 1 -or !$elements[0].InnerText.Trim()) {
            throw 'A WACK test has no result or requirement status.'
        }
        [ordered]@{
            index = $test.GetAttribute('INDEX')
            name = $test.GetAttribute('NAME')
            optional = $optional -ceq 'TRUE'
            result = $elements[0].InnerText.Trim()
        }
    }
    $required = @($tests | Where-Object { !$_.optional })
    if (!$required.Count) { throw 'The WACK report contains no required tests.' }
    $requiredNonPass = @($required | Where-Object { $_.result -cne 'PASS' })
    return [ordered]@{
        schemaVersion = 1
        overallResult = $overall
        partialRun = $partialRun -ceq 'TRUE'
        passed = $overall -ceq 'PASS' -and $partialRun -ceq 'FALSE' -and !$requiredNonPass.Count
        packageName = $root.GetAttribute('APP_NAME')
        packageVersion = $root.GetAttribute('APP_VERSION')
        toolkitVersion = $root.GetAttribute('VERSION')
        toolkitArchitecture = $root.GetAttribute('TOOLSET_ARCHITECTURE')
        windowsVersion = $root.GetAttribute('OSVERSION')
        requiredTestCount = $required.Count
        requiredNonPass = $requiredNonPass
        optionalNonPass = @($tests | Where-Object { $_.optional -and $_.result -cne 'PASS' })
    }
}

$xmlPath = [IO.Path]::GetFullPath($ReportPath)
if ($ReadExistingReport) {
    $summary = Read-WackReport $xmlPath
    $summary | ConvertTo-Json -Depth 6
    if (!$summary.passed) { throw 'The WACK report does not show a complete pass.' }
    return
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (![Environment]::UserInteractive -or !$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run the WACK preflight as an administrator in an interactive test session.'
}
$kit = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/App Certification Kit/appcert.exe'
if (!(Test-Path -LiteralPath $kit -PathType Leaf)) { throw 'The Windows App Certification Kit is missing from the test machine.' }
$package = (Resolve-Path -LiteralPath $SignedMsix).Path
$summaryPath = [IO.Path]::ChangeExtension($xmlPath, 'json')
if ([IO.Path]::GetExtension($package) -ine '.msix' -or [IO.Path]::GetExtension($xmlPath) -ine '.xml' -or
    (Test-Path -LiteralPath $xmlPath) -or (Test-Path -LiteralPath $summaryPath)) {
    throw 'Pass a signed MSIX and a new XML report path.'
}
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    if ($null -eq $archive.GetEntry('AppxSignature.p7x')) { throw 'The input MSIX does not contain a package signature.' }
    $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
    try { [xml]$manifest = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    $identity = $manifest.Package.Identity
}
finally { $archive.Dispose() }

$sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
$null = New-Item -Path (Split-Path -Parent $xmlPath) -ItemType Directory -Force
Push-Location -LiteralPath (Split-Path -Parent $kit)
try {
    & $kit reset *> ([IO.Path]::ChangeExtension($xmlPath, 'reset.log'))
    if ($LASTEXITCODE) { throw 'Could not reset the Windows App Certification Kit.' }
    & $kit test -appxpackagepath $package -reportoutputpath $xmlPath *> ([IO.Path]::ChangeExtension($xmlPath, 'test.log'))
    $testExitCode = $LASTEXITCODE
}
finally { Pop-Location }

if (!(Test-Path -LiteralPath $xmlPath -PathType Leaf)) { throw "WACK did not write a report (exit code $testExitCode)." }
$summary = Read-WackReport $xmlPath
$summary.testExitCode = $testExitCode
$summary.packageSha256 = $sha256
$summary.packageMatches = $summary.packageName -ceq $identity.Name -and $summary.packageVersion -ceq $identity.Version -and
    (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ceq $sha256
$summary.passed = $summary.passed -and $summary.packageMatches -and $testExitCode -eq 0
$summary | ConvertTo-Json -Depth 6 | Tee-Object -FilePath $summaryPath
if (!$summary.passed) { throw 'The MSIX WACK preflight did not pass. Review the XML and JSON reports.' }
