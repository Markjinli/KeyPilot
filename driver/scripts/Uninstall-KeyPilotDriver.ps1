[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^oem\d+\.inf$')]
    [string]$PublishedName,
    [switch]$IUnderstandThisRemovesAKernelDriver
)

$ErrorActionPreference = 'Stop'
if (-not $IUnderstandThisRemovesAKernelDriver) {
    throw 'Uninstall was not run. Supply -IUnderstandThisRemovesAKernelDriver after checking the exact oemNN.inf name.'
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw 'Run this recovery operation in an elevated PowerShell window.'
}

$windowsDirectory = [IO.Path]::GetDirectoryName([Environment]::SystemDirectory)
$dismModule = Join-Path $windowsDirectory 'System32\WindowsPowerShell\v1.0\Modules\Dism\Dism.psd1'
if (-not (Test-Path -LiteralPath $dismModule -PathType Leaf)) {
    throw "The trusted Windows DISM PowerShell module was not found: $dismModule"
}
Import-Module -Name $dismModule -Force -ErrorAction Stop
$driver = Dism\Get-WindowsDriver -Online -Driver $PublishedName
if (-not $driver -or
    [IO.Path]::GetFileName($driver.OriginalFileName) -ine 'KeyPilotFilter.inf' -or
    $driver.ProviderName -ne 'KeyPilot' -or $driver.ClassName -ne 'Extension') {
    throw "Refusing to remove $PublishedName because DISM did not verify it as the KeyPilot Extension package."
}

$pnputil = Join-Path ([Environment]::SystemDirectory) 'pnputil.exe'
if (-not (Test-Path -LiteralPath $pnputil -PathType Leaf)) {
    throw "The Windows PnP utility was not found: $pnputil"
}
& $pnputil /delete-driver $PublishedName /uninstall /force
$pnpUtilExitCode = $LASTEXITCODE
if ($pnpUtilExitCode -notin @(0, 3010)) {
    throw "PnPUtil failed with exit code $pnpUtilExitCode."
}
& $pnputil /scan-devices
if ($pnpUtilExitCode -eq 3010) {
    Write-Warning 'Windows accepted the removal and reported that a reboot is required.'
}
Write-Host 'Removal requested. Reboot before treating the keyboard stack as recovered.'
