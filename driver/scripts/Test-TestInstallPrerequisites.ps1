[CmdletBinding()]
param(
    [string]$PackageDirectory,
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64',
    [string]$NuGetPackagesRoot = 'E:\KeyPilotTools\nuget-packages',
    [switch]$IHaveExternalInputAndRecoveryMedia,
    [string]$DangerConfirmation
)

$ErrorActionPreference = 'Stop'

function Get-PeMachine([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Not a PE image: $Path" }
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0x40 -or $peOffset -gt $stream.Length - 6) { throw "Invalid PE header: $Path" }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "Invalid PE signature: $Path" }
        return $reader.ReadUInt16()
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $PSScriptRoot "..\KeyPilotFilter\$Platform\Release\KeyPilotFilter"
}
$package = [IO.Path]::GetFullPath($PackageDirectory)
$inf = Join-Path $package 'KeyPilotFilter.inf'
$sys = Join-Path $package 'KeyPilotFilter.sys'
$cat = Join-Path $package 'KeyPilotFilter.cat'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$pnputilPath = Join-Path ([Environment]::SystemDirectory) 'pnputil.exe'
$bcdeditPath = Join-Path ([Environment]::SystemDirectory) 'bcdedit.exe'
$pnputil = Test-Path -LiteralPath $pnputilPath -PathType Leaf
$bcdedit = if (Test-Path -LiteralPath $bcdeditPath -PathType Leaf) { $bcdeditPath } else { $null }
$testSigning = $null
if ($bcdedit) {
    $bcdText = (& $bcdedit /enum '{current}' 2>$null) -join "`n"
    if ($LASTEXITCODE -eq 0) {
        $testSigning = $bcdText -match '(?im)^testsigning\s+Yes\s*$'
    }
}
$secureBoot = $null
$windowsDirectory = [IO.Path]::GetDirectoryName([Environment]::SystemDirectory)
$secureBootModule = Join-Path $windowsDirectory 'System32\WindowsPowerShell\v1.0\Modules\SecureBoot\SecureBoot.psd1'
if (Test-Path -LiteralPath $secureBootModule -PathType Leaf) {
    try {
        Import-Module -Name $secureBootModule -Force -ErrorAction Stop
        $secureBoot = SecureBoot\Confirm-SecureBootUEFI -ErrorAction Stop
    } catch {
        $secureBoot = $null
    }
}

$sysSignature = if (Test-Path -LiteralPath $sys) { Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $sys } else { $null }
$catSignature = if (Test-Path -LiteralPath $cat) { Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $cat } else { $null }
$chainValid = $false
$trustedPublisher = $false
$hasCodeSigningEku = $false
if ($catSignature -and $catSignature.SignerCertificate) {
    $signer = $catSignature.SignerCertificate
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
    try {
        $chainValid = $chain.Build($signer)
    } finally {
        $chain.Dispose()
    }
    $hasCodeSigningEku = @($signer.Extensions | Where-Object {
        $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] -and
        @($_.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' }).Count -ne 0
    }).Count -ne 0
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::TrustedPublisher,
        [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $trustedPublisher = $store.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $signer.Thumbprint,
            $false).Count -ne 0
    } finally {
        $store.Dispose()
    }
}

# Catalog membership is enforced by the fixed System32 PnPUtil call. Never execute an SDK or
# NuGet signtool from this elevated script: adjacent DLL search would cross the UAC boundary.
$catalogCoverageEnforcedBy = 'PnPUtil /add-driver'
$peMachine = $null
$peMachineMatches = $false
if (Test-Path -LiteralPath $sys -PathType Leaf) {
    $peMachine = Get-PeMachine $sys
    $expectedMachine = if ($Platform -eq 'x64') { 0x8664 } else { 0xaa64 }
    $peMachineMatches = $peMachine -eq $expectedMachine
}

$checks = [ordered]@{
    ReadOnlyCheck = $true
    PackageDirectory = $package
    IsAdministrator = $isAdmin
    PnpUtilAvailable = [bool]$pnputil
    InfPresent = Test-Path -LiteralPath $inf -PathType Leaf
    SysPresent = Test-Path -LiteralPath $sys -PathType Leaf
    CatalogPresent = Test-Path -LiteralPath $cat -PathType Leaf
    SysSignature = if ($sysSignature) { [string]$sysSignature.Status } else { 'Missing' }
    CatalogSignature = if ($catSignature) { [string]$catSignature.Status } else { 'Missing' }
    CertificateChainValid = $chainValid
    CodeSigningEkuPresent = $hasCodeSigningEku
    SignerInLocalMachineTrustedPublisher = $trustedPublisher
    CatalogCoverageEnforcedBy = $catalogCoverageEnforcedBy
    RequestedPlatform = $Platform
    PeMachine = if ($null -ne $peMachine) { '0x{0:X4}' -f $peMachine } else { 'Missing' }
    PeMachineMatches = $peMachineMatches
    TestSigningEnabled = $testSigning
    SecureBootEnabled = $secureBoot
    RecoveryScriptPresent = Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Recover-KeyPilotDriver.cmd')
    ExternalInputAndRecoveryConfirmed = [bool]$IHaveExternalInputAndRecoveryMedia
    DangerTokenConfirmed = $DangerConfirmation -ceq 'KEYPILOT-TEST-DRIVER-RISK'
}
[pscustomobject]$checks | Format-List

$ready = $checks.IsAdministrator -and $checks.PnpUtilAvailable -and
         $checks.InfPresent -and $checks.SysPresent -and $checks.CatalogPresent -and
         $checks.SysSignature -eq 'Valid' -and $checks.CatalogSignature -eq 'Valid' -and
         $checks.CertificateChainValid -and $checks.CodeSigningEkuPresent -and
         $checks.SignerInLocalMachineTrustedPublisher -and
         $checks.PeMachineMatches -and $checks.TestSigningEnabled -eq $true -and
         $checks.SecureBootEnabled -eq $false -and $checks.RecoveryScriptPresent -and
         $checks.ExternalInputAndRecoveryConfirmed -and $checks.DangerTokenConfirmed
if (-not $ready) {
    throw 'TEST DRIVER prerequisites are not satisfied. Nothing was installed or changed.'
}

Write-Host 'All TEST DRIVER prerequisites passed. This script did not install or change anything.'
