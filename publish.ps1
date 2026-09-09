[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.2.0',
    [string]$DotnetPath = $env:KEYPILOT_DOTNET,
    [string]$NuGetPackagesRoot = $env:KEYPILOT_NUGET_PACKAGES,
    [switch]$BuildDriver,
    [switch]$SkipBuild,
    [switch]$RuntimeSmoke,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$packageName = 'KeyPilot-win-x64'
$finalDirectory = Join-Path $artifactsRoot $packageName
$zipPath = Join-Path $artifactsRoot "$packageName.zip"
$zipDigestPath = "$zipPath.sha256"
$stagingRoot = Join-Path $artifactsRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
$appPublish = Join-Path $stagingRoot 'app-publish'
$brokerPublish = Join-Path $stagingRoot 'broker-publish'
$packageStaging = Join-Path $stagingRoot $packageName
$backupDirectory = Join-Path $artifactsRoot ('.previous-' + [Guid]::NewGuid().ToString('N'))

function Assert-UnderArtifacts([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($artifactsRoot) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a package operation outside the artifacts directory: $fullPath"
    }
}

function Resolve-DotnetSdk([string]$RequestedPath) {
    $candidates = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidate = $RequestedPath
        if (Test-Path -LiteralPath $candidate -PathType Container) { $candidate = Join-Path $candidate 'dotnet.exe' }
        $candidates.Add($candidate)
    }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $candidates.Add($command.Source) }
    $candidates.Add('E:\KeyPilotTools\dotnet\dotnet.exe')
    $candidates.Add('C:\Program Files\dotnet\dotnet.exe')
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    throw '.NET SDK not found. Use -DotnetPath or KEYPILOT_DOTNET to point to dotnet.exe.'
}

function Invoke-Native([string]$Description, [string]$Executable, [string[]]$Arguments) {
    Write-Host "==> $Description"
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE." }
}

function Copy-Required([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Required packaging input is missing: $Source" }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Get-RelativePathUnderRoot([string]$Root, [string]$Candidate) {
    $separator = [IO.Path]::DirectorySeparatorChar
    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + $separator
    $normalizedCandidate = [IO.Path]::GetFullPath($Candidate).Replace(
        [IO.Path]::AltDirectorySeparatorChar, $separator)
    if (-not $normalizedCandidate.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the expected release root: $normalizedCandidate"
    }
    return $normalizedCandidate.Substring($normalizedRoot.Length)
}

function Get-ProcessesLoadedFrom([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return @() }
    @(
        Get-Process -Name 'KeyPilot.App', 'KeyPilot.DriverBroker' -ErrorAction SilentlyContinue | Where-Object {
            try {
                $null = Get-RelativePathUnderRoot $Root $_.MainModule.FileName
                $true
            } catch { $false }
        }
    )
}

function Assert-DirectoryIsNotReparsePoint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force
    $points = @($item)
    if ($item.PSIsContainer) { $points += @(Get-ChildItem -LiteralPath $Path -Force -Recurse) }
    $points = @($points | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($points.Count -ne 0) {
        throw "Refusing a release operation on a link or reparse point: $($points.FullName -join ', ')"
    }
}

function Assert-ExistingPathChainIsNotReparsePoint([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not (Test-Path -LiteralPath $current)) {
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing a release path whose existing ancestor is a link or reparse point: $current"
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}

foreach ($path in @($stagingRoot, $finalDirectory, $zipPath, $zipDigestPath, $backupDirectory)) {
    Assert-UnderArtifacts $path
}
Assert-ExistingPathChainIsNotReparsePoint $artifactsRoot
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
Assert-ExistingPathChainIsNotReparsePoint $artifactsRoot

$DotnetPath = Resolve-DotnetSdk $DotnetPath
if ([string]::IsNullOrWhiteSpace($NuGetPackagesRoot)) {
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        $NuGetPackagesRoot = $env:NUGET_PACKAGES
    } elseif (Test-Path -LiteralPath 'E:\KeyPilotTools\nuget-packages' -PathType Container) {
        $NuGetPackagesRoot = 'E:\KeyPilotTools\nuget-packages'
    }
}
if (-not [string]::IsNullOrWhiteSpace($NuGetPackagesRoot)) {
    $env:NUGET_PACKAGES = [IO.Path]::GetFullPath($NuGetPackagesRoot)
    New-Item -ItemType Directory -Path $env:NUGET_PACKAGES -Force | Out-Null
}
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$buildArguments = @{
    Configuration = $Configuration
    DotnetPath = $DotnetPath
}
if (-not [string]::IsNullOrWhiteSpace($NuGetPackagesRoot)) {
    $buildArguments.NuGetPackagesRoot = $NuGetPackagesRoot
}
if ($BuildDriver) {
    $buildArguments.BuildDriver = $true
}

try {
    if (-not $SkipBuild) {
        Write-Host '==> Run verified build and tests'
        & (Join-Path $projectRoot 'build.ps1') @buildArguments
        if (-not $?) { throw 'Verified build and tests failed.' }
    }

    New-Item -ItemType Directory -Path $appPublish, $brokerPublish, $packageStaging -Force | Out-Null
    $commonPublish = @(
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '--nologo',
        '--no-restore',
        '-p:DebugSymbols=false',
        '-p:DebugType=None',
        "-p:Version=$Version",
        '-p:NoWarn=NU1801'
    )
    Invoke-Native 'Publish self-contained WinUI app' $DotnetPath (@(
        'publish', (Join-Path $projectRoot 'src\KeyPilot.App\KeyPilot.App.csproj'),
        '-o', $appPublish
    ) + $commonPublish)
    Invoke-Native 'Publish self-contained elevated broker' $DotnetPath (@(
        'publish', (Join-Path $projectRoot 'driver\user\KeyPilot.DriverBroker\KeyPilot.DriverBroker.csproj'),
        '-o', $brokerPublish
    ) + $commonPublish)

    Copy-Item -Path (Join-Path $appPublish '*') -Destination $packageStaging -Recurse -Force
    $brokerDirectory = Join-Path $packageStaging 'broker'
    New-Item -ItemType Directory -Path $brokerDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $brokerPublish '*') -Destination $brokerDirectory -Recurse -Force
    Get-ChildItem -LiteralPath $packageStaging -File -Recurse -Filter '*.pdb' | Remove-Item -Force

    # Windows App SDK ships native WinUI MUI satellites for every supported culture. KeyPilot's
    # release supports English and Simplified Chinese only, so keep those two exact resource sets.
    $supportedWinUiCultures = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $null = $supportedWinUiCultures.Add('en-US')
    $null = $supportedWinUiCultures.Add('zh-CN')
    $winUiCultureDirectories = @(Get-ChildItem -LiteralPath $packageStaging -Directory | Where-Object {
        Test-Path -LiteralPath (Join-Path $_.FullName 'Microsoft.ui.xaml.dll.mui') -PathType Leaf
    })
    foreach ($cultureDirectory in $winUiCultureDirectories) {
        if (-not $supportedWinUiCultures.Contains($cultureDirectory.Name)) {
            Remove-Item -LiteralPath $cultureDirectory.FullName -Recurse -Force
        }
    }

    Write-Host 'Companions are no longer staged into the KeyPilot package.'


    $driverInput = Join-Path $projectRoot 'driver\KeyPilotFilter\x64\Release\KeyPilotFilter'
    $driverDirectory = Join-Path $packageStaging 'driver-package'
    $driverBinaryDirectory = Join-Path $driverDirectory 'KeyPilotFilter\x64\Release\KeyPilotFilter'
    New-Item -ItemType Directory -Path (Join-Path $driverDirectory 'scripts'), $driverBinaryDirectory -Force | Out-Null
    $driverOutputs = @('KeyPilotFilter.inf', 'KeyPilotFilter.sys', 'keypilotfilter.cat') |
        ForEach-Object { Join-Path $driverInput $_ }
    foreach ($output in $driverOutputs) {
        if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
            throw "Driver output is missing. Run publish.cmd -BuildDriver: $output"
        }
    }
    $driverSources = @(
        Get-ChildItem -LiteralPath (Join-Path $projectRoot 'driver\KeyPilotFilter') -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/]x64[\\/]' }
        Get-Item -LiteralPath (Join-Path $projectRoot 'driver\include\KeyPilotProtocol.h')
    )
    $newestDriverSource = ($driverSources | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
    $oldestDriverOutput = ($driverOutputs | Get-Item | Sort-Object LastWriteTimeUtc | Select-Object -First 1).LastWriteTimeUtc
    if ($oldestDriverOutput -lt $newestDriverSource) {
        throw 'Driver output is older than its source. Re-run publish.cmd -BuildDriver to prevent a stale SYS/INF/CAT package.'
    }
    foreach ($name in @('KeyPilotFilter.inf', 'KeyPilotFilter.sys', 'keypilotfilter.cat')) {
        Copy-Required (Join-Path $driverInput $name) (Join-Path $driverBinaryDirectory $name)
    }
    foreach ($name in @(
        'Install-KeyPilotDriver.ps1',
        'Install-KeyPilotTestDriver.ps1',
        'New-KeyPilotTestCertificate.ps1',
        'Sign-KeyPilotTestPackage.ps1',
        'Test-InstallPrerequisites.ps1',
        'Test-TestInstallPrerequisites.ps1',
        'Uninstall-KeyPilotDriver.ps1',
        'Recover-KeyPilotDriver.cmd'
    )) {
        Copy-Required (Join-Path $projectRoot "driver\scripts\$name") (Join-Path $driverDirectory "scripts\$name")
    }
    Copy-Required (Join-Path $projectRoot 'driver\scripts\Recover-KeyPilotDriver.cmd') (Join-Path $driverDirectory 'Recover-KeyPilotDriver.cmd')
    Copy-Required (Join-Path $projectRoot 'driver\TEST-SIGNING-PACKAGE.md') (Join-Path $driverDirectory 'TEST-SIGNING.md')
    Copy-Required (Join-Path $projectRoot 'driver\README-PACKAGE.txt') (Join-Path $driverDirectory 'README-驱动包.txt')

    $installerDirectory = Join-Path $packageStaging 'installer'
    New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null
    foreach ($name in @('Install-KeyPilot.ps1', 'Uninstall-KeyPilot.ps1')) {
        Copy-Required (Join-Path $projectRoot "installer\$name") (Join-Path $installerDirectory $name)
    }
    foreach ($name in @('安装 KeyPilot.cmd', '卸载 KeyPilot.cmd', '检查发布包.cmd')) {
        Copy-Required (Join-Path $projectRoot "installer\$name") (Join-Path $packageStaging $name)
    }

    $appPath = Join-Path $packageStaging 'KeyPilot.App.exe'
    $brokerPath = Join-Path $brokerDirectory 'KeyPilot.DriverBroker.exe'
    $driverInfPath = Join-Path $driverBinaryDirectory 'KeyPilotFilter.inf'
    $driverPath = Join-Path $driverBinaryDirectory 'KeyPilotFilter.sys'
    $driverCatPath = Join-Path $driverBinaryDirectory 'keypilotfilter.cat'
    $brokerHash = (Get-FileHash -LiteralPath $brokerPath -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(
        (Join-Path $brokerDirectory 'KeyPilot.DriverBroker.sha256'),
        $brokerHash + [Environment]::NewLine,
        [Text.Encoding]::ASCII)

    $launcher = @'
@echo off
setlocal
cd /d "%~dp0"
if not exist "KeyPilot.App.exe" (
  echo [ERROR] KeyPilot.App.exe is missing. Please extract the complete release archive first.
  pause
  exit /b 2
)
start "" "%~dp0KeyPilot.App.exe"
if errorlevel 1 (
  echo [ERROR] Windows could not start KeyPilot. See 运行说明.txt for diagnostics.
  pause
  exit /b 1
)
exit /b 0
'@
    [IO.File]::WriteAllText((Join-Path $packageStaging '启动 KeyPilot.cmd'), $launcher, [Text.UTF8Encoding]::new($false))
    Copy-Required (Join-Path $projectRoot 'docs\RUNNING.txt') (Join-Path $packageStaging '运行说明.txt')
    Copy-Required (Join-Path $projectRoot 'tools\Test-ReleasePackage.ps1') (Join-Path $packageStaging 'Verify-Release.ps1')

    $appSignature = Get-AuthenticodeSignature -LiteralPath $appPath
    $brokerSignature = Get-AuthenticodeSignature -LiteralPath $brokerPath
    $driverSignature = Get-AuthenticodeSignature -LiteralPath $driverPath
    $driverCatSignature = Get-AuthenticodeSignature -LiteralPath $driverCatPath
    $driverVerMatch = [regex]::Match(
        (Get-Content -LiteralPath $driverInfPath -Raw -Encoding utf8),
        '(?im)^DriverVer\s*=\s*[^,]+,([^\r\n]+)\r?$')
    if (-not $driverVerMatch.Success) { throw 'Driver INF has no parseable DriverVer.' }
    $release = [ordered]@{
        schemaVersion = 1
        product = 'KeyPilot'
        version = $Version
        channel = 'local-self-use'
        configuration = $Configuration
        runtimeIdentifier = 'win-x64'
        minimumWindowsVersion = '10.0.17763.0'
        selfContained = $true
        supportedLanguages = @('en-US', 'zh-CN')
        publishedUtc = [DateTime]::UtcNow.ToString('O')
        toolchain = [ordered]@{
            dotnetSdk = (& $DotnetPath --version).Trim()
            windowsAppSdk = '2.3.1'
            wdkSdkNuGet = '10.0.28000.2526'
        }
        app = [ordered]@{
            file = 'KeyPilot.App.exe'
            icon = 'KeyPilot.ico'
            sha256 = (Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash
            authenticodeStatus = [string]$appSignature.Status
        }
        broker = [ordered]@{
            file = 'broker/KeyPilot.DriverBroker.exe'
            sha256 = $brokerHash
            authenticodeStatus = [string]$brokerSignature.Status
            protectedLocationRequired = $true
        }
        driver = [ordered]@{
            file = 'driver-package/KeyPilotFilter/x64/Release/KeyPilotFilter/KeyPilotFilter.sys'
            infDriverVersion = $driverVerMatch.Groups[1].Value.Trim()
            infSha256 = (Get-FileHash -LiteralPath $driverInfPath -Algorithm SHA256).Hash
            sysSha256 = (Get-FileHash -LiteralPath $driverPath -Algorithm SHA256).Hash
            catSha256 = (Get-FileHash -LiteralPath $driverCatPath -Algorithm SHA256).Hash
            authenticodeStatus = [string]$driverSignature.Status
            catalogAuthenticodeStatus = [string]$driverCatSignature.Status
            builtThisPublishRun = [bool]$BuildDriver
            installed = $false
        }
        safety = [ordered]@{
            driverInstalledByPublish = $false
            securitySettingsChangedByPublish = $false
            unsignedDriverCannotLoadOnNormalSecuredWindows = $true
        }
    }
    $release | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $packageStaging 'release.json') -Encoding utf8

    $manifestPath = Join-Path $packageStaging 'MANIFEST.sha256'
    $manifestLines = Get-ChildItem -LiteralPath $packageStaging -File -Recurse |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = (Get-RelativePathUnderRoot $packageStaging $_.FullName).Replace('\', '/')
            '{0} *{1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $relative
        }
    [IO.File]::WriteAllLines($manifestPath, $manifestLines, [Text.UTF8Encoding]::new($false))

    & (Join-Path $projectRoot 'tools\Test-ReleasePackage.ps1') `
        -PackageDirectory $packageStaging `
        -ExpectedVersion $Version `
        -RuntimeSmoke:$RuntimeSmoke
    if (-not $?) { throw 'Release package verification failed.' }

    $loaded = @(Get-ProcessesLoadedFrom $finalDirectory)
    if ($loaded.Count -ne 0) {
        $processList = ($loaded | ForEach-Object { "$($_.ProcessName) PID $($_.Id)" }) -join ', '
        throw "Close the current artifact process before publishing; it was not terminated: $processList"
    }
    Assert-DirectoryIsNotReparsePoint $finalDirectory
    if (Test-Path -LiteralPath $finalDirectory) {
        Move-Item -LiteralPath $finalDirectory -Destination $backupDirectory
    }
    Move-Item -LiteralPath $packageStaging -Destination $finalDirectory
    if (-not $NoZip) {
        if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
        if (Test-Path -LiteralPath $zipDigestPath) { Remove-Item -LiteralPath $zipDigestPath -Force }
        Compress-Archive -LiteralPath $finalDirectory -DestinationPath $zipPath -CompressionLevel Optimal
        $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        [IO.File]::WriteAllText($zipDigestPath, $zipHash + ' *' + [IO.Path]::GetFileName($zipPath) + [Environment]::NewLine, [Text.Encoding]::ASCII)
        Write-Host "Release archive: $zipPath"
        Write-Host "Archive SHA-256: $zipHash"
    }
    Write-Host "Verified release directory: $finalDirectory"
    if (Test-Path -LiteralPath $backupDirectory) {
        try { Remove-Item -LiteralPath $backupDirectory -Recurse -Force }
        catch { Write-Warning "The new release is valid, but the previous artifact backup remains at: $backupDirectory" }
    }
} catch {
    if ((Test-Path -LiteralPath $backupDirectory) -and -not (Test-Path -LiteralPath $finalDirectory)) {
        Move-Item -LiteralPath $backupDirectory -Destination $finalDirectory
    }
    throw
} finally {
    if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
}
