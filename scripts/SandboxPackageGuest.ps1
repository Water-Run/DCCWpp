# Runs only inside a disposable Windows Sandbox instance.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = 'C:\DccwPackageSmoke'
$inputRoot = 'C:\DccwInput'
$outputRoot = 'C:\DccwResult'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if ($identity.User.Value -ne 'S-1-5-18' -or !(Test-Path -LiteralPath $inputRoot) -or !(Test-Path -LiteralPath $outputRoot)) {
    throw 'This script expects the explicitly mapped disposable Windows Sandbox.'
}

$result = [ordered]@{ success = $false; scope = 'provisioning'; step = 'setup'; osBuild = [Environment]::OSVersion.Version.Build }
$log = Join-Path $testRoot 'dism.log'
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $guestPackage = Join-Path $testRoot 'DccwPlus.msix'
    Copy-Item -LiteralPath (Join-Path $inputRoot 'DccwPlus.msix') -Destination $guestPackage
    $result.inputSha256 = (Get-FileHash -LiteralPath $guestPackage -Algorithm SHA256).Hash

    $result.step = 'sign'
    $signing = @{ Type = 'CodeSigningCert'; Subject = 'CN=DCCWPlus-Development'
        CertStoreLocation = 'Cert:\LocalMachine\My'; KeyExportPolicy = 'NonExportable' }
    $certificate = New-SelfSignedCertificate @signing
    $cerPath = Join-Path $testRoot 'Development.cer'
    Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null
    Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    $signLog = & (Join-Path $inputRoot 'signtool.exe') sign /sm /sha1 $certificate.Thumbprint /fd SHA256 $guestPackage 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Could not sign the isolated development package. $signLog" }
    $result.signed = $true

    $result.step = 'provision'
    Add-AppxProvisionedPackage -Online -PackagePath $guestPackage -SkipLicense -LogPath $log | Out-Null
    $provisioned = Get-AppxProvisionedPackage -Online -LogPath $log |
        Where-Object DisplayName -EQ 'DCCWPlus.Development'
    if ($null -eq $provisioned) { throw 'Windows did not list the development package as provisioned.' }
    $result.packageName = $provisioned.PackageName
    $result.provisioned = $true

    $result.step = 'remove_provisioning'
    Remove-AppxProvisionedPackage -Online -PackageName $provisioned.PackageName -LogPath $log | Out-Null
    if (Get-AppxProvisionedPackage -Online -LogPath $log | Where-Object DisplayName -EQ 'DCCWPlus.Development') {
        throw 'Windows still lists the package as provisioned.'
    }
    $result.provisioningRemoved = $true
    $result.step = 'complete'
    $result.success = $true
}
catch {
    $result.error = $_.Exception.GetBaseException().Message
    if (Test-Path -LiteralPath $log) {
        Get-Content -LiteralPath $log -Tail 80 | Set-Content -LiteralPath (Join-Path $outputRoot 'dism.log') -Encoding UTF8
    }
}
finally {
    $temporary = Join-Path $outputRoot 'result.tmp'
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination (Join-Path $outputRoot 'result.json')
}
