[CmdletBinding()]
param(
    [string]$PackageDirectory,
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64',
    [string]$NuGetPackagesRoot = 'E:\KeyPilotTools\nuget-packages'
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
# Do not execute SDK/NuGet tools from an elevated prerequisite check. A genuine
# signtool.exe can still load an attacker-controlled adjacent DLL when its directory is writable.
# PnPUtil is the trusted Windows component that enforces catalog membership during installation.
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
    CatalogCoverageEnforcedBy = $catalogCoverageEnforcedBy
    RequestedPlatform = $Platform
    PeMachine = if ($null -ne $peMachine) { '0x{0:X4}' -f $peMachine } else { 'Missing' }
    PeMachineMatches = $peMachineMatches
    TestSigningEnabled = $testSigning
    SecureBootEnabled = $secureBoot
    RecoveryScriptPresent = Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Recover-KeyPilotDriver.cmd')
}

[pscustomobject]$checks | Format-List

$ready = $checks.IsAdministrator -and $checks.PnpUtilAvailable -and $checks.InfPresent -and
         $checks.SysPresent -and $checks.CatalogPresent -and
         $checks.CatalogSignature -eq 'Valid' -and
         $checks.PeMachineMatches -and
         $checks.TestSigningEnabled -eq $false -and $checks.RecoveryScriptPresent
if (-not $ready) {
    throw 'Prerequisites are not satisfied. Nothing was installed or changed.'
}

Write-Host 'All installation prerequisites passed. This script did not install anything.'
