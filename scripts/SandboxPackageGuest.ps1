# Runs only inside a disposable Windows Sandbox instance.
[CmdletBinding()]
param([switch]$StandardUser)

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

function Sign-DevelopmentPackage([string]$Path, [string]$Thumbprint) {
    $signLog = & (Join-Path $inputRoot 'signtool.exe') sign /sm /sha1 $Thumbprint /fd SHA256 $Path 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Could not sign the isolated development package. $signLog" }
}

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
    Sign-DevelopmentPackage $guestPackage $certificate.Thumbprint
    $result.signed = $true

    if ($StandardUser) {
        $result.scope = 'standard-user'
        $upgradeInput = Join-Path $inputRoot 'DccwPlusUpgrade.msix'
        if (Test-Path -LiteralPath $upgradeInput) {
            $result.step = 'sign_upgrade'
            $guestUpgrade = Join-Path $testRoot 'DccwPlusUpgrade.msix'
            Copy-Item -LiteralPath $upgradeInput -Destination $guestUpgrade
            $result.upgradeInputSha256 = (Get-FileHash -LiteralPath $guestUpgrade).Hash
            Sign-DevelopmentPackage $guestUpgrade $certificate.Thumbprint
        }
        $result.step = 'standard_user'
        $userOutput = Join-Path $testRoot 'user'
        New-Item -ItemType Directory -Path $userOutput | Out-Null
        $runner = Join-Path $testRoot 'SandboxStandardUserGuest.ps1'
        Copy-Item -LiteralPath (Join-Path $inputRoot 'SandboxStandardUserGuest.ps1') -Destination $runner
        $profiles = Get-ChildItem -LiteralPath $inputRoot -Filter 'DCCWPlus-*.icc' -File
        foreach ($profile in $profiles) { Copy-Item -LiteralPath $profile.FullName -Destination $testRoot }

        $password = 'Q!' + [Guid]::NewGuid().ToString('N') + '9z'
        $user = New-LocalUser -Name 'DccwSmokeUser' -Password (ConvertTo-SecureString $password -AsPlainText -Force)
        if (!(Get-LocalGroupMember -SID 'S-1-5-32-545' | Where-Object SID -EQ $user.SID)) {
            Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $user
        }
        & icacls.exe $userOutput /grant "*$($user.SID):(OI)(CI)M" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not provide an isolated results directory for the standard user.' }

        $result.step = 'user_process'
        Add-Type -Path (Join-Path $inputRoot 'SandboxUserProcess.cs')
        $result.userExitCode = [SandboxUserProcess]::Run($user.Name, $password, "$PSHOME\powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -File $runner", $testRoot)
        $userReport = Join-Path $userOutput 'result.json'
        if ($result.userExitCode -ne 0 -or !(Test-Path -LiteralPath $userReport)) {
            throw ('The standard-user process did not produce a report (0x{0:X8}).' -f $result.userExitCode)
        }
        $result.userCheck = Get-Content -LiteralPath $userReport -Raw | ConvertFrom-Json
        $result.step = $result.userCheck.step
        $result.success = $result.userCheck.success
        return
    }

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
