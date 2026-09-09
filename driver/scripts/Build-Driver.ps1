[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64',
    [ValidatePattern('^10\.0\.28000\.\d+$')]
    [string]$WdkPackageVersion = '10.0.28000.2526',
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

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw 'Visual Studio Installer (vswhere.exe) was not found.'
}
$requirements = @(
    'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
    'Component.Microsoft.Windows.DriverKit'
)
if ($Platform -eq 'ARM64') {
    $requirements += 'Microsoft.VisualStudio.Component.VC.Tools.ARM64'
}
$installationPath = & $vswhere -latest -products * -requires $requirements -property installationPath
if (-not $installationPath) {
    throw "Visual Studio C++ components for $Platform were not found."
}
$msbuildRoot = & $vswhere -latest -products * -requires $requirements -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
if (-not $msbuildRoot) {
    throw 'MSBuild with C++ tools was not found.'
}
$nativeMsBuild = Join-Path (Split-Path -Parent $msbuildRoot) 'amd64\MSBuild.exe'
$msbuild = if (Test-Path -LiteralPath $nativeMsBuild -PathType Leaf) { $nativeMsBuild } else { $msbuildRoot }

$driverToolset = & $vswhere -latest -products * -requires $requirements -find "MSBuild\Microsoft\VC\**\Platforms\$Platform\PlatformToolsets\WindowsKernelModeDriver10.0\Toolset.props" | Select-Object -First 1
if (-not $driverToolset) {
    throw "The Windows Driver Kit MSBuild integration for $Platform was not found."
}

$nugetRoot = [IO.Path]::GetFullPath($NuGetPackagesRoot)
New-Item -ItemType Directory -Path $nugetRoot -Force | Out-Null
$env:NUGET_PACKAGES = $nugetRoot
$wdkPackageId = if ($Platform -eq 'x64') { 'Microsoft.Windows.WDK.x64' } else { 'Microsoft.Windows.WDK.ARM64' }
$sdkPackageId = if ($Platform -eq 'x64') { 'Microsoft.Windows.SDK.cpp.x64' } else { 'Microsoft.Windows.SDK.cpp.ARM64' }
$targetPlatformVersion = '10.0.28000.0'
$solution = Join-Path $PSScriptRoot '..\KeyPilotDriver.sln'
& $msbuild $solution /t:Restore "/p:Configuration=$Configuration" "/p:Platform=$Platform" "/p:WindowsTargetPlatformVersion=$targetPlatformVersion" "/p:RestorePackagesPath=$nugetRoot" /warnaserror
if ($LASTEXITCODE -ne 0) {
    throw "Driver NuGet restore failed with exit code $LASTEXITCODE."
}

$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\KeyPilotFilter'))
$assetsPath = Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'obj') -Recurse -Filter project.assets.json -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -ExpandProperty FullName -First 1
if (-not $assetsPath) {
    throw 'NuGet restore did not produce project.assets.json.'
}
$assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
$libraryNames = @($assets.libraries.PSObject.Properties.Name)
foreach ($expectedLibrary in @("$wdkPackageId/$WdkPackageVersion", "$sdkPackageId/$WdkPackageVersion")) {
    if ($libraryNames -notcontains $expectedLibrary) {
        throw "The resolved driver toolchain is not the pinned package $expectedLibrary. Assets: $assetsPath"
    }
}
$wdkPackagePath = Join-Path $nugetRoot "$(($wdkPackageId).ToLowerInvariant())\$WdkPackageVersion"
$sdkPackagePath = Join-Path $nugetRoot "$(($sdkPackageId).ToLowerInvariant())\$WdkPackageVersion"
foreach ($packagePath in @($wdkPackagePath, $sdkPackagePath)) {
    if (-not (Test-Path -LiteralPath $packagePath -PathType Container)) {
        throw "Pinned NuGet toolchain package folder is missing: $packagePath"
    }
}
$infVerif = Get-ChildItem -LiteralPath $wdkPackagePath -Recurse -Filter infverif.exe -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object FullName | Select-Object -ExpandProperty FullName -First 1
if (-not $infVerif) {
    $infVerif = Get-ChildItem -LiteralPath $wdkPackagePath -Recurse -Filter infverif.exe -File -ErrorAction SilentlyContinue |
        Sort-Object FullName | Select-Object -ExpandProperty FullName -First 1
}
if (-not $infVerif) {
    throw "InfVerif was not found in pinned WDK package $wdkPackageId/$WdkPackageVersion."
}

& $msbuild $solution /m /t:Build "/p:Configuration=$Configuration" "/p:Platform=$Platform" "/p:WindowsTargetPlatformVersion=$targetPlatformVersion" "/p:RestorePackagesPath=$nugetRoot" /p:EnableTestSign=false /p:SignMode=Off /warnaserror
if ($LASTEXITCODE -ne 0) {
    throw "Driver build failed with exit code $LASTEXITCODE."
}

$outputDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\KeyPilotFilter\$Platform\$Configuration\KeyPilotFilter"))
$sys = Join-Path $outputDirectory 'KeyPilotFilter.sys'
$inf = Join-Path $outputDirectory 'KeyPilotFilter.inf'
$cat = Join-Path $outputDirectory 'KeyPilotFilter.cat'
foreach ($artifact in @($sys, $inf, $cat)) {
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "Expected isolated $Platform artifact is missing: $artifact"
    }
}
foreach ($unsignedArtifact in @($sys, $cat)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $unsignedArtifact
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::NotSigned -or
        $null -ne $signature.SignerCertificate) {
        throw "The ordinary build unexpectedly produced or retained a signed artifact: $unsignedArtifact"
    }
}

$machine = Get-PeMachine $sys
$expectedMachine = if ($Platform -eq 'x64') { 0x8664 } else { 0xaa64 }
if ($machine -ne $expectedMachine) {
    throw ('PE machine mismatch: expected 0x{0:X4}, found 0x{1:X4}.' -f $expectedMachine, $machine)
}

& $infVerif /w $inf
if ($LASTEXITCODE -ne 0) {
    throw "InfVerif /w failed with exit code $LASTEXITCODE."
}

Write-Host ('Build and InfVerif completed for {0}; PE machine 0x{1:X4}; WDK/SDK NuGet {2}. Unsigned package: {3}. Cache: {4}. No driver was installed and no boot setting was changed.' -f $Platform, $machine, $WdkPackageVersion, $outputDirectory, $nugetRoot)
