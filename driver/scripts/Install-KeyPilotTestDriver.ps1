[CmdletBinding()]
param(
    [string]$PackageDirectory,
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64',
    [string]$NuGetPackagesRoot = 'E:\KeyPilotTools\nuget-packages',
    [switch]$IHaveExternalInputAndRecoveryMedia,
    [string]$DangerConfirmation,
    [switch]$IUnderstandThisInstallsATestSignedKernelDriver
)

$ErrorActionPreference = 'Stop'
if (-not $IUnderstandThisInstallsATestSignedKernelDriver) {
    throw 'TEST DRIVER installation was not run. Supply the explicit installation switch after reviewing recovery.'
}
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $PSScriptRoot "..\KeyPilotFilter\$Platform\Release\KeyPilotFilter"
}
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw 'Run this TEST driver installation operation in an elevated PowerShell window.'
}

$sourcePackage = [IO.Path]::GetFullPath($PackageDirectory)
$sourceInf = Join-Path $sourcePackage 'KeyPilotFilter.inf'
$commonApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
if ([string]::IsNullOrWhiteSpace($commonApplicationData)) {
    throw 'The protected ProgramData recovery-record location is unavailable.'
}

function New-RecoveryDirectorySecurity {
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($administrators)
    foreach ($identity in @($administrators, $system)) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $identity,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow))
    }
    return $security
}

function Assert-ProtectedRecoveryDirectory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Protected recovery directory is missing: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing a recovery-record path that traverses a reparse point: $Path"
    }
    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) {
        throw "Recovery directory inherits access rules: $Path"
    }
    $trustedSids = @('S-1-5-18', 'S-1-5-32-544')
    $ownerSid = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($ownerSid -notin $trustedSids) {
        throw "Recovery directory owner is not SYSTEM or Administrators: $Path ($ownerSid)"
    }
    $accessRules = $acl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier])
    $unsafe = @($accessRules | Where-Object {
        $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
        $_.IdentityReference.Value -notin $trustedSids -and
        ($_.FileSystemRights -band (
            [Security.AccessControl.FileSystemRights]::Write -bor
            [Security.AccessControl.FileSystemRights]::Delete -bor
            [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
            [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
            [Security.AccessControl.FileSystemRights]::TakeOwnership)) -ne 0
    })
    if ($unsafe.Count -ne 0) {
        throw "Recovery directory is writable by a principal other than SYSTEM or Administrators: $Path"
    }
}

function New-ProtectedRecoveryDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        Assert-ProtectedRecoveryDirectory $Path
        return
    }
    try {
        [IO.DirectoryInfo]::new($Path).Create((New-RecoveryDirectorySecurity))
    } catch [IO.IOException] {
        # A concurrent creator is accepted only if the resulting path is already protected.
    }
    Assert-ProtectedRecoveryDirectory $Path
}
$programDataItem = Get-Item -LiteralPath $commonApplicationData -Force
if (($programDataItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Refusing a reparse-point CommonApplicationData root: $commonApplicationData"
}
$keyPilotDataRoot = Join-Path $commonApplicationData 'KeyPilot'
New-ProtectedRecoveryDirectory $keyPilotDataRoot
$stagingRoot = Join-Path $keyPilotDataRoot 'DriverStaging'
New-ProtectedRecoveryDirectory $stagingRoot
$stagedPackage = Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))
New-ProtectedRecoveryDirectory $stagedPackage

try {
    foreach ($name in @('KeyPilotFilter.inf', 'KeyPilotFilter.sys', 'KeyPilotFilter.cat')) {
        [IO.File]::Copy((Join-Path $sourcePackage $name), (Join-Path $stagedPackage $name), $false)
    }

    # Execute only a protected, hash-pinned helper; the release directory may be writable
    # by the unelevated account while this administrator process is running.
    $checkScript = Join-Path $stagedPackage 'Test-TestInstallPrerequisites.ps1'
    $recoveryScript = Join-Path $stagedPackage 'Recover-KeyPilotDriver.cmd'
    [IO.File]::Copy((Join-Path $PSScriptRoot 'Test-TestInstallPrerequisites.ps1'), $checkScript, $false)
    [IO.File]::Copy((Join-Path $PSScriptRoot 'Recover-KeyPilotDriver.cmd'), $recoveryScript, $false)
    $expectedCheckHash = 'F8FDE725691DB583BBCD9F74FBB7DCB79CE3514D35A90FF8BCA685A3EB355785'
    $expectedRecoveryHash = '7EFB6ABB6ACA4898D0B6D793E792C58CF6B97DCEB2E0DB728409E3E711FD42E1'
    $actualCheckHash = (Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $checkScript -Algorithm SHA256).Hash
    $actualRecoveryHash = (Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $recoveryScript -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualCheckHash, $expectedCheckHash, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($actualRecoveryHash, $expectedRecoveryHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A pinned TEST-driver companion changed during staging. Nothing was installed.'
    }

    & $checkScript -PackageDirectory $stagedPackage -Platform $Platform -NuGetPackagesRoot $NuGetPackagesRoot `
        -IHaveExternalInputAndRecoveryMedia:$IHaveExternalInputAndRecoveryMedia `
        -DangerConfirmation $DangerConfirmation

    $package = $stagedPackage
    $inf = Join-Path $package 'KeyPilotFilter.inf'
    $sys = Join-Path $package 'KeyPilotFilter.sys'
    $cat = Join-Path $package 'KeyPilotFilter.cat'
$recordRoot = Join-Path $keyPilotDataRoot 'Recovery'
New-ProtectedRecoveryDirectory $recordRoot
$recordPath = Join-Path $recordRoot 'test-installation-record.json'

function Write-RecoveryRecord([object]$Value) {
    $temporary = Join-Path $recordRoot ('.test-installation-record.{0}.tmp' -f [Guid]::NewGuid().ToString('N'))
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 4))
    $stream = [IO.FileStream]::new(
        $temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally {
        $stream.Dispose()
    }
    try {
        Move-Item -LiteralPath $temporary -Destination $recordPath -Force
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Get-KeyPilotDriverPackages {
    @(Dism\Get-WindowsDriver -Online | Where-Object {
        [IO.Path]::GetFileName($_.OriginalFileName) -ieq 'KeyPilotFilter.inf' -and
        $_.ProviderName -eq 'KeyPilot' -and $_.ClassName -eq 'Extension'
    } | Sort-Object Date -Descending)
}

$windowsDirectory = [IO.Path]::GetDirectoryName([Environment]::SystemDirectory)
$dismModule = Join-Path $windowsDirectory 'System32\WindowsPowerShell\v1.0\Modules\Dism\Dism.psd1'
if (-not (Test-Path -LiteralPath $dismModule -PathType Leaf)) {
    throw "The trusted Windows DISM PowerShell module was not found: $dismModule"
}
Import-Module -Name $dismModule -Force -ErrorAction Stop

$beforeMatching = @(Get-KeyPilotDriverPackages)
$beforePublishedNames = @($beforeMatching.Driver)
$pendingRecord = [ordered]@{
    Status = 'Pending-TestDriver'
    RequestedAtUtc = [DateTime]::UtcNow.ToString('O')
    PublishedName = $null
    OriginalInf = $sourceInf
    InfSha256 = (Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $inf -Algorithm SHA256).Hash
    SysSha256 = (Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $sys -Algorithm SHA256).Hash
    CatalogSha256 = (Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $cat -Algorithm SHA256).Hash
    BeforeVerifiedPublishedNames = $beforePublishedNames
    RecoveryCommand = 'Run Recover-KeyPilotDriver.cmd interactively; enter the verified oemNN.inf and KEYPILOT-RECOVERY.'
}
Write-RecoveryRecord $pendingRecord

Write-Warning 'INSTALLING TEST-SIGNED KERNEL DRIVER. Keep external input and recovery media connected.'
$pnputil = Join-Path ([Environment]::SystemDirectory) 'pnputil.exe'
if (-not (Test-Path -LiteralPath $pnputil -PathType Leaf)) {
    throw "The Windows PnP utility was not found: $pnputil"
}
& $pnputil /add-driver $inf /install
$pnpUtilExitCode = $LASTEXITCODE
$rebootRequired = $pnpUtilExitCode -eq 3010
$pendingRecord['PnPUtilExitCode'] = $pnpUtilExitCode
$pendingRecord['RebootRequired'] = $rebootRequired
if ($pnpUtilExitCode -notin @(0, 3010)) {
    $pendingRecord.Status = 'PnPUtilFailed-TestDriver'
    Write-RecoveryRecord $pendingRecord
    throw "PnPUtil failed with exit code $pnpUtilExitCode."
}

$matching = @()
try {
    $matching = @(Get-KeyPilotDriverPackages)
} catch {
    $pendingRecord.Status = 'Installed-TestDriver-VerificationFailed'
    $pendingRecord['InstalledAtUtc'] = [DateTime]::UtcNow.ToString('O')
    Write-RecoveryRecord $pendingRecord
    throw
}
$newMatching = @($matching | Where-Object { $beforePublishedNames -notcontains $_.Driver })
if ($newMatching.Count -ne 1) {
    $pendingRecord.Status = 'Installed-TestDriver-PublishedNameAmbiguous'
    $pendingRecord['AllVerifiedPublishedNames'] = @($matching.Driver)
    $pendingRecord['NewVerifiedPublishedNames'] = @($newMatching.Driver)
    $pendingRecord['InstalledAtUtc'] = [DateTime]::UtcNow.ToString('O')
    Write-RecoveryRecord $pendingRecord
    throw "PnPUtil succeeded, but exactly one new verified KeyPilot package was not found. Do not guess an oemNN.inf. Record: $recordPath"
}

$installedRecord = [ordered]@{}
foreach ($entry in $pendingRecord.GetEnumerator()) { $installedRecord[$entry.Key] = $entry.Value }
$installedRecord.Status = 'Installed-TestDriver'
$installedRecord.PublishedName = $newMatching[0].Driver
$installedRecord['AllVerifiedPublishedNames'] = @($matching.Driver)
$installedRecord['NewVerifiedPublishedNames'] = @($newMatching.Driver)
$installedRecord['InstalledAtUtc'] = [DateTime]::UtcNow.ToString('O')
$installedRecord.RecoveryCommand = "Run Recover-KeyPilotDriver.cmd interactively; enter $($newMatching[0].Driver) and KEYPILOT-RECOVERY."
Write-RecoveryRecord $installedRecord

Write-Host "TEST DRIVER installation request completed. Recovery information: $recordPath"
if ($rebootRequired) {
    Write-Warning 'Windows accepted the TEST package and reported that a reboot is required.'
}
Write-Warning 'This script did not and will never change TESTSIGNING, Secure Boot, or certificate stores.'
} finally {
    if (Test-Path -LiteralPath $stagedPackage -PathType Container) {
        Remove-Item -LiteralPath $stagedPackage -Recurse -Force
    }
}
