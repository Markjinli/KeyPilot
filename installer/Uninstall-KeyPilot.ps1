[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$destination = [IO.Path]::GetFullPath((Join-Path $programFiles 'KeyPilot'))
$expected = [IO.Path]::GetFullPath($programFiles) + [IO.Path]::DirectorySeparatorChar + 'KeyPilot'
if (-not $destination.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing an unexpected uninstall target: $destination"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This uninstaller must run as administrator. Use “卸载 KeyPilot.cmd” to request UAC.'
}

if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
    Write-Host 'KeyPilot App/Broker is not installed in Program Files.'
    exit 0
}
$destinationItem = Get-Item -LiteralPath $destination -Force
$reparsePoints = @($destinationItem)
$reparsePoints += @(Get-ChildItem -LiteralPath $destination -Force -Recurse)
$reparsePoints = @($reparsePoints | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
if ($reparsePoints.Count -ne 0) {
    throw "Refusing to remove an installation target containing a link or reparse point: $($reparsePoints.FullName -join ', ')"
}
$releasePath = Join-Path $destination 'release.json'
if (-not (Test-Path -LiteralPath $releasePath -PathType Leaf)) { throw 'Refusing to remove a directory without KeyPilot release metadata.' }
$release = Get-Content -LiteralPath $releasePath -Raw -Encoding utf8 | ConvertFrom-Json
if ($release.product -ne 'KeyPilot' -or $release.runtimeIdentifier -ne 'win-x64') {
    throw 'Refusing to remove a directory whose release metadata is not KeyPilot win-x64.'
}

function Test-PathInsideRoot([string]$Root, [string]$Candidate) {
    $separator = [IO.Path]::DirectorySeparatorChar
    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + $separator
    $normalizedCandidate = [IO.Path]::GetFullPath($Candidate).Replace(
        [IO.Path]::AltDirectorySeparatorChar, $separator)
    return $normalizedCandidate.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)
}

$running = @(Get-Process -Name 'KeyPilot.App', 'KeyPilot.DriverBroker' -ErrorAction SilentlyContinue | Where-Object {
    try {
        Test-PathInsideRoot $destination $_.MainModule.FileName
    } catch { $false }
})
if ($running.Count -ne 0) { throw 'Close KeyPilot App/Broker before uninstalling, then run the uninstaller again.' }

$commonApplicationData = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::CommonApplicationData)
if ([string]::IsNullOrWhiteSpace($commonApplicationData)) {
    throw 'Windows CommonApplicationData directory was not found.'
}
$shortcut = Join-Path $commonApplicationData 'Microsoft\Windows\Start Menu\Programs\KeyPilot.lnk'
if (Test-Path -LiteralPath $shortcut -PathType Leaf) { Remove-Item -LiteralPath $shortcut -Force }
Remove-Item -LiteralPath $destination -Recurse -Force
Write-Host 'KeyPilot App/Broker was removed from Program Files.'
Write-Warning 'The keyboard driver was not changed. If it was installed separately, use the verified driver recovery/uninstall procedure.'
