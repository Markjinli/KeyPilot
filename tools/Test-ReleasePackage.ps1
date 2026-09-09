[CmdletBinding()]
param(
    [string]$PackageDirectory = (Join-Path $PSScriptRoot '..\artifacts\KeyPilot-win-x64'),
    [string]$ExpectedVersion,
    [switch]$RuntimeSmoke,
    [ValidateRange(5, 60)]
    [int]$StartupTimeoutSeconds = 15,
    [ValidateRange(5, 60)]
    [int]$ShutdownTimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'

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

function Assert-Release([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw "Release package check failed: $Message"
    }
}

function Get-PeMachine([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        Assert-Release ($reader.ReadUInt16() -eq 0x5a4d) "Not a PE image: $Path"
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        Assert-Release ($peOffset -ge 0x40 -and $peOffset -le $stream.Length - 6) "Invalid PE header: $Path"
        $stream.Position = $peOffset
        Assert-Release ($reader.ReadUInt32() -eq 0x00004550) "Invalid PE signature: $Path"
        return $reader.ReadUInt16()
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-IcoSizes([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        Assert-Release ($reader.ReadUInt16() -eq 0) "Invalid ICO reserved field: $Path"
        Assert-Release ($reader.ReadUInt16() -eq 1) "Invalid ICO image type: $Path"
        $count = $reader.ReadUInt16()
        Assert-Release ($count -gt 0 -and $stream.Length -ge (6 + (16 * $count))) "Invalid ICO directory: $Path"
        $sizes = [Collections.Generic.List[string]]::new()
        for ($index = 0; $index -lt $count; $index++) {
            $width = $reader.ReadByte()
            $height = $reader.ReadByte()
            if ($width -eq 0) { $width = 256 }
            if ($height -eq 0) { $height = 256 }
            $null = $reader.ReadByte()
            $null = $reader.ReadByte()
            $null = $reader.ReadUInt16()
            $null = $reader.ReadUInt16()
            $bytes = $reader.ReadUInt32()
            $offset = $reader.ReadUInt32()
            Assert-Release ($bytes -gt 0 -and $offset -lt $stream.Length -and $offset + $bytes -le $stream.Length) `
                "ICO image entry is outside the file: $Path"
            $sizes.Add("${width}x${height}")
        }
        return @($sizes)
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

$package = [IO.Path]::GetFullPath($PackageDirectory)
Assert-Release (Test-Path -LiteralPath $package -PathType Container) "Package directory is missing: $package"
$pathCursor = $package
while (-not [string]::IsNullOrWhiteSpace($pathCursor)) {
    $pathItem = Get-Item -LiteralPath $pathCursor -Force
    Assert-Release (($pathItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
        "Release path contains a link or reparse-point ancestor: $pathCursor"
    $pathParent = [IO.Directory]::GetParent($pathCursor)
    if ($null -eq $pathParent) { break }
    $pathCursor = $pathParent.FullName
}
$reparsePoints = @((Get-Item -LiteralPath $package -Force))
$reparsePoints += @(Get-ChildItem -LiteralPath $package -Force -Recurse)
$reparsePoints = @($reparsePoints | Where-Object {
    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
})
Assert-Release ($reparsePoints.Count -eq 0) `
    "Release package must not contain links or reparse points: $($reparsePoints.FullName -join ', ')"

$required = @(
    'KeyPilot.App.exe',
    'KeyPilot.App.dll',
    'KeyPilot.App.deps.json',
    'KeyPilot.App.runtimeconfig.json',
    'KeyPilot.App.pri',
    'hostfxr.dll',
    'hostpolicy.dll',
    'coreclr.dll',
    'System.Private.CoreLib.dll',
    'Microsoft.WindowsAppRuntime.dll',
    'Microsoft.ui.xaml.dll',
    'KeyPilot.ico',
    'en-us\Microsoft.ui.xaml.dll.mui',
    'en-us\Microsoft.UI.Xaml.Phone.dll.mui',
    'zh-CN\Microsoft.ui.xaml.dll.mui',
    'zh-CN\Microsoft.UI.Xaml.Phone.dll.mui',
    '启动 KeyPilot.cmd',
    '运行说明.txt',
    'release.json',
    'MANIFEST.sha256',
    'Verify-Release.ps1',
    'broker\KeyPilot.DriverBroker.exe',
    'broker\KeyPilot.DriverBroker.dll',
    'broker\KeyPilot.DriverBroker.runtimeconfig.json',
    'broker\hostfxr.dll',
    'broker\hostpolicy.dll',
    'broker\coreclr.dll',
    'broker\System.Private.CoreLib.dll',
    'broker\KeyPilot.DriverBroker.sha256',
    'driver-package\KeyPilotFilter\x64\Release\KeyPilotFilter\KeyPilotFilter.inf',
    'driver-package\KeyPilotFilter\x64\Release\KeyPilotFilter\KeyPilotFilter.sys',
    'driver-package\KeyPilotFilter\x64\Release\KeyPilotFilter\keypilotfilter.cat',
    'driver-package\README-驱动包.txt',
    'driver-package\TEST-SIGNING.md',
    'driver-package\Recover-KeyPilotDriver.cmd',
    '安装 KeyPilot.cmd',
    '卸载 KeyPilot.cmd',
    '检查发布包.cmd',
    'installer\Install-KeyPilot.ps1',
    'installer\Uninstall-KeyPilot.ps1'
)
foreach ($relative in $required) {
    Assert-Release (Test-Path -LiteralPath (Join-Path $package $relative) -PathType Leaf) "Required file is missing: $relative"
}

$winUiMuiFiles = @(Get-ChildItem -LiteralPath $package -File -Recurse -Filter 'Microsoft.ui.xaml.dll.mui')
$winUiMuiCultures = @($winUiMuiFiles | ForEach-Object {
    (Get-Item -LiteralPath $_.DirectoryName).Name.ToLowerInvariant()
} | Sort-Object -Unique)
Assert-Release ($winUiMuiCultures.Count -eq 2) `
    "Expected exactly the English and Simplified Chinese WinUI resource folders, found: $($winUiMuiCultures -join ', ')"
Assert-Release ($winUiMuiCultures -contains 'en-us') 'English WinUI resources are missing.'
Assert-Release ($winUiMuiCultures -contains 'zh-cn') 'Simplified Chinese WinUI resources are missing.'

Assert-Release (-not (Test-Path -LiteralPath (Join-Path $package 'companions') -PathType Container)) `
    'Release package must not stage GPL companion executables.'

$appPath = Join-Path $package 'KeyPilot.App.exe'
$iconPath = Join-Path $package 'KeyPilot.ico'
$brokerPath = Join-Path $package 'broker\KeyPilot.DriverBroker.exe'
$driverInfPath = Join-Path $package 'driver-package\KeyPilotFilter\x64\Release\KeyPilotFilter\KeyPilotFilter.inf'
$driverPath = Join-Path $package 'driver-package\KeyPilotFilter\x64\Release\KeyPilotFilter\KeyPilotFilter.sys'
$driverCatPath = Join-Path $package 'driver-package\KeyPilotFilter\x64\Release\KeyPilotFilter\keypilotfilter.cat'
Assert-Release ((Get-PeMachine $appPath) -eq 0x8664) 'KeyPilot.App.exe is not x64.'
Assert-Release ((Get-PeMachine $brokerPath) -eq 0x8664) 'KeyPilot.DriverBroker.exe is not x64.'
Assert-Release ((Get-PeMachine $driverPath) -eq 0x8664) 'KeyPilotFilter.sys is not x64.'
$iconSizes = @(Get-IcoSizes $iconPath)
foreach ($requiredIconSize in @('16x16', '20x20', '24x24', '32x32', '40x40', '48x48', '64x64', '128x128', '256x256')) {
    Assert-Release ($iconSizes -contains $requiredIconSize) "KeyPilot.ico is missing $requiredIconSize."
}

if (-not ('KeyPilot.IconNativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace KeyPilot {
    public static class IconNativeMethods {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint icons);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr icon);
    }
}
'@
}
$embeddedIconCount = [KeyPilot.IconNativeMethods]::ExtractIconEx($appPath, -1, $null, $null, 0)
Assert-Release ($embeddedIconCount -gt 0) 'KeyPilot.App.exe does not contain an embedded application icon.'

$runtimeConfig = Get-Content -LiteralPath (Join-Path $package 'KeyPilot.App.runtimeconfig.json') -Raw -Encoding utf8 |
    ConvertFrom-Json
$includedFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
Assert-Release ($includedFrameworks.Count -gt 0) 'App runtimeconfig does not identify an included framework.'
Assert-Release ($includedFrameworks.name -contains 'Microsoft.NETCore.App') 'App does not contain the .NET runtime framework declaration.'

$brokerRuntimeConfig = Get-Content -LiteralPath (Join-Path $package 'broker\KeyPilot.DriverBroker.runtimeconfig.json') -Raw -Encoding utf8 |
    ConvertFrom-Json
Assert-Release (@($brokerRuntimeConfig.runtimeOptions.includedFrameworks).name -contains 'Microsoft.NETCore.App') `
    'Broker does not contain the .NET runtime framework declaration.'

$release = Get-Content -LiteralPath (Join-Path $package 'release.json') -Raw -Encoding utf8 | ConvertFrom-Json
Assert-Release ($release.schemaVersion -eq 1) 'Unsupported release.json schema.'
Assert-Release ($release.product -eq 'KeyPilot') 'release.json product does not match KeyPilot.'
Assert-Release ($release.runtimeIdentifier -eq 'win-x64') 'release.json runtimeIdentifier is not win-x64.'
Assert-Release ($release.selfContained -eq $true) 'release.json does not declare a self-contained release.'
$releaseLanguages = @($release.supportedLanguages | ForEach-Object { ([string]$_).ToLowerInvariant() } | Sort-Object -Unique)
Assert-Release ($releaseLanguages.Count -eq 2 -and
    $releaseLanguages -contains 'en-us' -and
    $releaseLanguages -contains 'zh-cn') 'release.json must declare only English and Simplified Chinese.'
Assert-Release ($release.app.icon -eq 'KeyPilot.ico') 'release.json does not identify the application icon.'
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    Assert-Release ($release.version -eq $ExpectedVersion) "Expected version $ExpectedVersion, found $($release.version)."
}

$appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appPath).ProductVersion
$brokerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($brokerPath).ProductVersion
Assert-Release ($appVersion -eq $release.version) "App ProductVersion $appVersion does not match release $($release.version)."
Assert-Release ($brokerVersion -eq $release.version) "Broker ProductVersion $brokerVersion does not match release $($release.version)."

$appHash = (Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash
$brokerHash = (Get-FileHash -LiteralPath $brokerPath -Algorithm SHA256).Hash
$driverHash = (Get-FileHash -LiteralPath $driverPath -Algorithm SHA256).Hash
Assert-Release ($appHash -eq $release.app.sha256) 'App SHA-256 does not match release.json.'
Assert-Release ($brokerHash -eq $release.broker.sha256) 'Broker SHA-256 does not match release.json.'
Assert-Release ($driverHash -eq $release.driver.sysSha256) 'Driver SHA-256 does not match release.json.'
Assert-Release ((Get-FileHash -LiteralPath $driverInfPath -Algorithm SHA256).Hash -eq $release.driver.infSha256) `
    'Driver INF SHA-256 does not match release.json.'
Assert-Release ((Get-FileHash -LiteralPath $driverCatPath -Algorithm SHA256).Hash -eq $release.driver.catSha256) `
    'Driver CAT SHA-256 does not match release.json.'
$driverVerMatch = [regex]::Match(
    (Get-Content -LiteralPath $driverInfPath -Raw -Encoding utf8),
    '(?im)^DriverVer\s*=\s*[^,]+,([^\r\n]+)\r?$')
Assert-Release $driverVerMatch.Success 'Driver INF has no parseable DriverVer.'
Assert-Release ($driverVerMatch.Groups[1].Value.Trim() -eq $release.driver.infDriverVersion) `
    'Driver INF version does not match release.json.'

$brokerDigest = (Get-Content -LiteralPath (Join-Path $package 'broker\KeyPilot.DriverBroker.sha256') -Raw -Encoding ascii).Trim()
Assert-Release ($brokerDigest -cmatch '^[A-F0-9]{64}$') 'Broker digest manifest must be exactly one uppercase SHA-256 value.'
Assert-Release ($brokerDigest -eq $brokerHash) 'Broker digest manifest does not match the executable.'

$manifestPath = Join-Path $package 'MANIFEST.sha256'
$entries = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in Get-Content -LiteralPath $manifestPath -Encoding utf8) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $match = [regex]::Match($line, '^([A-F0-9]{64}) \*(.+)$')
    Assert-Release $match.Success "Malformed MANIFEST.sha256 line: $line"
    $relative = $match.Groups[2].Value.Replace('/', '\')
    Assert-Release (-not [IO.Path]::IsPathRooted($relative)) "Manifest contains a rooted path: $relative"
    Assert-Release (-not ($relative -split '[\\/]' | Where-Object { $_ -eq '..' })) "Manifest contains traversal: $relative"
    Assert-Release (-not $entries.ContainsKey($relative)) "Manifest contains duplicate entry: $relative"
    $entries.Add($relative, $match.Groups[1].Value)
}

$files = @(Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object { $_.FullName -ne $manifestPath })
Assert-Release ($entries.Count -eq $files.Count) "Manifest has $($entries.Count) entries but package has $($files.Count) payload files."
foreach ($file in $files) {
    $relative = Get-RelativePathUnderRoot $package $file.FullName
    Assert-Release ($entries.ContainsKey($relative)) "File is absent from manifest: $relative"
    $actual = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    Assert-Release ($entries[$relative] -eq $actual) "SHA-256 mismatch: $relative"
}

$forbidden = @(Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object {
    $_.Extension -in @('.pdb', '.cs', '.csproj', '.pfx', '.cer', '.snk') -or
    $_.Name -in @('Build-Driver.ps1', 'project.assets.json')
})
Assert-Release ($forbidden.Count -eq 0) "Development/private files leaked into package: $($forbidden.FullName -join ', ')"

$appSignature = Get-AuthenticodeSignature -LiteralPath $appPath
$brokerSignature = Get-AuthenticodeSignature -LiteralPath $brokerPath
$driverSignature = Get-AuthenticodeSignature -LiteralPath $driverPath
$driverCatSignature = Get-AuthenticodeSignature -LiteralPath $driverCatPath
Assert-Release ([string]$appSignature.Status -eq [string]$release.app.authenticodeStatus) 'App signature status differs from release.json.'
Assert-Release ([string]$brokerSignature.Status -eq [string]$release.broker.authenticodeStatus) 'Broker signature status differs from release.json.'
Assert-Release ([string]$driverSignature.Status -eq [string]$release.driver.authenticodeStatus) 'Driver signature status differs from release.json.'
Assert-Release ([string]$driverCatSignature.Status -eq [string]$release.driver.catalogAuthenticodeStatus) `
    'Driver catalog signature status differs from release.json.'

$totalBytes = ($files | Measure-Object Length -Sum).Sum + (Get-Item -LiteralPath $manifestPath).Length

if ($RuntimeSmoke) {
    $localLogDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'KeyPilot'
    $startupLog = Join-Path $localLogDirectory 'startup-error.log'
    $runtimeLog = Join-Path $localLogDirectory 'runtime.log'
    $runtimeRollover = "$runtimeLog.1"
    $beforeLog = if (Test-Path -LiteralPath $startupLog -PathType Leaf) {
        $item = Get-Item -LiteralPath $startupLog
        [pscustomobject]@{
            Exists = $true
            Length = $item.Length
            LastWriteTimeUtc = $item.LastWriteTimeUtc
            Sha256 = (Get-FileHash -LiteralPath $startupLog -Algorithm SHA256).Hash
        }
    } else {
        [pscustomobject]@{ Exists = $false; Length = 0; LastWriteTimeUtc = [DateTime]::MinValue; Sha256 = '' }
    }
    $beforeRuntimeBytes = if (Test-Path -LiteralPath $runtimeLog -PathType Leaf) {
        [IO.File]::ReadAllBytes($runtimeLog)
    } else { $null }
    $beforeRolloverHash = if (Test-Path -LiteralPath $runtimeRollover -PathType Leaf) {
        (Get-FileHash -LiteralPath $runtimeRollover -Algorithm SHA256).Hash
    } else { $null }

    if (-not ('KeyPilot.ReleaseNativeMethods' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace KeyPilot {
    public static class ReleaseNativeMethods {
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
'@
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $appPath
    $startInfo.WorkingDirectory = $package
    $startInfo.UseShellExecute = $false
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        Assert-Release $process.Start() 'Runtime smoke could not start KeyPilot.App.exe.'
        $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
        $window = [IntPtr]::Zero
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($process.HasExited) {
                throw "Runtime smoke process exited before showing a window (exit code $($process.ExitCode))."
            }
            $process.Refresh()
            $window = $process.MainWindowHandle
            if ($window -ne [IntPtr]::Zero -and [KeyPilot.ReleaseNativeMethods]::IsWindowVisible($window)) { break }
            Start-Sleep -Milliseconds 100
        }
        Assert-Release ($window -ne [IntPtr]::Zero -and [KeyPilot.ReleaseNativeMethods]::IsWindowVisible($window)) `
            "Runtime smoke did not find a visible KeyPilot window within $StartupTimeoutSeconds seconds (PID $($process.Id))."
        Assert-Release ([KeyPilot.ReleaseNativeMethods]::PostMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) `
            "Runtime smoke could not send WM_CLOSE to PID $($process.Id)."
        Assert-Release ($process.WaitForExit($ShutdownTimeoutSeconds * 1000)) `
            "Runtime smoke window closed but PID $($process.Id) did not exit within $ShutdownTimeoutSeconds seconds; it was not terminated."
        Assert-Release ($process.ExitCode -eq 0) "Runtime smoke process returned exit code $($process.ExitCode)."
    } finally {
        $process.Dispose()
    }

    $afterLog = if (Test-Path -LiteralPath $startupLog -PathType Leaf) {
        $item = Get-Item -LiteralPath $startupLog
        [pscustomobject]@{
            Exists = $true
            Length = $item.Length
            LastWriteTimeUtc = $item.LastWriteTimeUtc
            Sha256 = (Get-FileHash -LiteralPath $startupLog -Algorithm SHA256).Hash
        }
    } else {
        [pscustomobject]@{ Exists = $false; Length = 0; LastWriteTimeUtc = [DateTime]::MinValue; Sha256 = '' }
    }
    $logChanged = $beforeLog.Exists -ne $afterLog.Exists -or $beforeLog.Length -ne $afterLog.Length -or
        $beforeLog.LastWriteTimeUtc -ne $afterLog.LastWriteTimeUtc -or $beforeLog.Sha256 -ne $afterLog.Sha256
    Assert-Release (-not $logChanged) "Runtime smoke changed the startup error log: $startupLog"

    $afterRolloverHash = if (Test-Path -LiteralPath $runtimeRollover -PathType Leaf) {
        (Get-FileHash -LiteralPath $runtimeRollover -Algorithm SHA256).Hash
    } else { $null }
    Assert-Release ($beforeRolloverHash -eq $afterRolloverHash) `
        "Runtime smoke rolled or changed the previous runtime log: $runtimeRollover"
    $afterRuntimeBytes = if (Test-Path -LiteralPath $runtimeLog -PathType Leaf) {
        [IO.File]::ReadAllBytes($runtimeLog)
    } else { $null }
    $newRuntimeBytes = [byte[]]@()
    if ($null -eq $beforeRuntimeBytes) {
        if ($null -ne $afterRuntimeBytes) { $newRuntimeBytes = $afterRuntimeBytes }
    } elseif ($null -eq $afterRuntimeBytes -or $afterRuntimeBytes.Length -lt $beforeRuntimeBytes.Length) {
        throw "Runtime smoke removed or truncated the runtime log: $runtimeLog"
    } else {
        $prefixMatches = $true
        for ($index = 0; $index -lt $beforeRuntimeBytes.Length; $index++) {
            if ($beforeRuntimeBytes[$index] -ne $afterRuntimeBytes[$index]) { $prefixMatches = $false; break }
        }
        Assert-Release $prefixMatches "Runtime smoke replaced existing runtime log content: $runtimeLog"
        if ($afterRuntimeBytes.Length -gt $beforeRuntimeBytes.Length) {
            $newRuntimeBytes = [byte[]]::new($afterRuntimeBytes.Length - $beforeRuntimeBytes.Length)
            [Array]::Copy($afterRuntimeBytes, $beforeRuntimeBytes.Length, $newRuntimeBytes, 0, $newRuntimeBytes.Length)
        }
    }
    if ($newRuntimeBytes.Length -ne 0) {
        $newRuntimeText = [Text.Encoding]::UTF8.GetString($newRuntimeBytes)
        $unexpectedLines = @($newRuntimeText -split '\r?\n' | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            $_ -notmatch '^\d{4}-\d{2}-\d{2}T.+ \[DriverSuppression\] .+$'
        })
        Assert-Release ($unexpectedLines.Count -eq 0) `
            "Runtime smoke logged an unexpected backend/action/shutdown error: $($unexpectedLines -join ' | ')"
    }
    Write-Host 'Runtime smoke passed: visible window, graceful WM_CLOSE, exit 0, no startup error and no unexpected runtime error.'
}

[pscustomobject]@{
    Result = 'PASS'
    Product = $release.product
    Version = $release.version
    RuntimeIdentifier = $release.runtimeIdentifier
    SelfContained = $release.selfContained
    PayloadFiles = $files.Count + 1
    TotalMiB = [Math]::Round($totalBytes / 1MB, 2)
    AppSha256 = $appHash
    BrokerSha256 = $brokerHash
    DriverSha256 = $driverHash
    DriverSignature = [string]$driverSignature.Status
    RuntimeSmoke = [bool]$RuntimeSmoke
} | Format-List
