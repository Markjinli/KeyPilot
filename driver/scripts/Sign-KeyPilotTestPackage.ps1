[CmdletBinding()]
param(
    [string]$PackageDirectory,
    [string]$PfxPath,
    [Security.SecureString]$PfxPassword,
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64',
    [ValidatePattern('^10\.0\.28000\.\d+$')]
    [string]$WdkPackageVersion = '10.0.28000.2526',
    [string]$NuGetPackagesRoot = 'E:\KeyPilotTools\nuget-packages',
    [switch]$IUnderstandThisCreatesATestSignedKernelPackage
)

$ErrorActionPreference = 'Stop'
if (-not $IUnderstandThisCreatesATestSignedKernelPackage) {
    throw 'Nothing was signed. Supply -IUnderstandThisCreatesATestSignedKernelPackage for the isolated test package.'
}
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $PSScriptRoot "..\KeyPilotFilter\$Platform\Release\KeyPilotFilter"
}
if ([string]::IsNullOrWhiteSpace($PfxPath)) {
    $PfxPath = Join-Path $PSScriptRoot '..\artifacts\test-signing\KeyPilot-Local-Test.pfx'
}
$package = [IO.Path]::GetFullPath($PackageDirectory)
$pfx = [IO.Path]::GetFullPath($PfxPath)
$inf = Join-Path $package 'KeyPilotFilter.inf'
$sys = Join-Path $package 'KeyPilotFilter.sys'
$cat = Join-Path $package 'KeyPilotFilter.cat'
foreach ($path in @($package, $pfx, $inf, $sys)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required test-signing input is missing: $path" }
}
if (-not $PfxPassword) {
    $PfxPassword = Read-Host 'Password for the local test PFX' -AsSecureString
}

$packageId = if ($Platform -eq 'x64') { 'microsoft.windows.wdk.x64' } else { 'microsoft.windows.wdk.arm64' }
$sdkPackageId = if ($Platform -eq 'x64') { 'microsoft.windows.sdk.cpp.x64' } else { 'microsoft.windows.sdk.cpp.arm64' }
$nugetRoot = [IO.Path]::GetFullPath($NuGetPackagesRoot)
$wdkPackage = Join-Path $nugetRoot "$packageId\$WdkPackageVersion"
$sdkPackage = Join-Path $nugetRoot "$sdkPackageId\$WdkPackageVersion"
$signTool = Get-ChildItem -LiteralPath @($wdkPackage, $sdkPackage) -Recurse -Filter signtool.exe -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object FullName | Select-Object -ExpandProperty FullName -First 1
$inf2Cat = Get-ChildItem -LiteralPath $wdkPackage -Recurse -Filter Inf2Cat.exe -File -ErrorAction SilentlyContinue |
    Sort-Object FullName | Select-Object -ExpandProperty FullName -First 1
if (-not $signTool -or -not $inf2Cat) {
    throw "WDK NuGet $packageId/$WdkPackageVersion SignTool/Inf2Cat was not found."
}

$passwordPointer = [IntPtr]::Zero
try {
    $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($PfxPassword)
    $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringUni($passwordPointer)

    # SYS must be signed before Inf2Cat hashes it into the catalog.
    & $signTool sign /fd SHA256 /f $pfx /p $plainPassword $sys
    if ($LASTEXITCODE -ne 0) { throw "Signing SYS failed with exit code $LASTEXITCODE." }

    $inf2CatOs = if ($Platform -eq 'x64') { '10_X64' } else { '10_ARM64' }
    & $inf2Cat "/driver:$package" "/os:$inf2CatOs"
    if ($LASTEXITCODE -ne 0) { throw "Inf2Cat failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $cat -PathType Leaf)) { throw "Inf2Cat did not create $cat" }

    & $signTool sign /fd SHA256 /f $pfx /p $plainPassword $cat
    if ($LASTEXITCODE -ne 0) { throw "Signing CAT failed with exit code $LASTEXITCODE." }
} finally {
    if ($passwordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($passwordPointer)
    }
}

Write-Host "Test-signed package prepared in $package"
Write-Warning 'No certificate was trusted, no BCD/Secure Boot setting was changed, and no driver was installed.'
