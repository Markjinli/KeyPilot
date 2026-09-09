[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DotnetPath = $env:KEYPILOT_DOTNET,
    [string]$NuGetPackagesRoot = $env:KEYPILOT_NUGET_PACKAGES,
    [switch]$BuildDriver
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

function Resolve-DotnetSdk {
    param([string]$RequestedPath)

    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidate = $RequestedPath
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $candidate = Join-Path $candidate 'dotnet.exe'
        }
        $candidates.Add($candidate)
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        $candidates.Add($command.Source)
    }

    $candidates.Add('E:\KeyPilotTools\dotnet\dotnet.exe')
    $candidates.Add('C:\Program Files\dotnet\dotnet.exe')

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw '.NET SDK not found. Use -DotnetPath or KEYPILOT_DOTNET to point to dotnet.exe.'
}

function Invoke-DotnetStep {
    param(
        [string]$Description,
        [string[]]$Arguments
    )

    Write-Host "==> $Description"
    & $script:DotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Invoke-PowerShellStep {
    param(
        [string]$Description,
        [string]$ScriptPath,
        [object[]]$Arguments = @()
    )

    Write-Host "==> $Description"
    & $ScriptPath @Arguments
    $stepSucceeded = $?
    $nativeExitCode = $LASTEXITCODE
    if (-not $stepSucceeded -or ($null -ne $nativeExitCode -and $nativeExitCode -ne 0)) {
        $displayCode = if ($null -eq $nativeExitCode) { 'PowerShell error' } else { $nativeExitCode }
        throw "$Description failed: $displayCode."
    }
}

$DotnetPath = Resolve-DotnetSdk $DotnetPath
if ([string]::IsNullOrWhiteSpace($NuGetPackagesRoot)) {
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        $NuGetPackagesRoot = $env:NUGET_PACKAGES
    } elseif (Test-Path -LiteralPath 'E:\KeyPilotTools\nuget-packages' -PathType Container) {
        $NuGetPackagesRoot = 'E:\KeyPilotTools\nuget-packages'
    }
}
if (-not [string]::IsNullOrWhiteSpace($NuGetPackagesRoot)) {
    $NuGetPackagesRoot = [IO.Path]::GetFullPath($NuGetPackagesRoot)
    New-Item -ItemType Directory -Path $NuGetPackagesRoot -Force | Out-Null
    $env:NUGET_PACKAGES = $NuGetPackagesRoot
    Write-Host "Using NuGet package cache: $NuGetPackagesRoot"
}
$projects = [ordered]@{
    CoreTests = 'tests\KeyPilot.Core.Tests\KeyPilot.Core.Tests.csproj'
    PlatformTests = 'tests\KeyPilot.Platform.Windows.Tests\KeyPilot.Platform.Windows.Tests.csproj'
    PolicyTests = 'driver\tests\PolicyModelTests\PolicyModelTests.csproj'
    BrokerTests = 'driver\tests\KeyPilot.DriverBroker.Tests\KeyPilot.DriverBroker.Tests.csproj'
    Broker = 'driver\user\KeyPilot.DriverBroker\KeyPilot.DriverBroker.csproj'
    App = 'src\KeyPilot.App\KeyPilot.App.csproj'
}

Invoke-PowerShellStep `
    -Description 'Verify XML and UTF-8 source files' `
    -ScriptPath (Join-Path $projectRoot 'tools\Verify-Source.ps1')

foreach ($entry in $projects.GetEnumerator()) {
    $absoluteProject = Join-Path $projectRoot $entry.Value
    Invoke-DotnetStep `
        -Description "Restore $($entry.Key)" `
        -Arguments @(
            'restore', $absoluteProject,
            '--ignore-failed-sources',
            '-p:NuGetAudit=false',
            '-p:NoWarn=NU1801'
        )
}

foreach ($testName in @('CoreTests', 'PlatformTests', 'PolicyTests', 'BrokerTests')) {
    Invoke-DotnetStep `
        -Description "Run $testName" `
        -Arguments @(
            'run',
            '--project', (Join-Path $projectRoot $projects[$testName]),
            '-c', $Configuration,
            '--no-restore'
        )
}

Invoke-PowerShellStep `
    -Description 'Run driver static model checks' `
    -ScriptPath (Join-Path $projectRoot 'driver\tests\Test-DriverStaticModel.ps1')

foreach ($buildName in @('Broker', 'App')) {
    Invoke-DotnetStep `
        -Description "Build $buildName" `
        -Arguments @(
            'build',
            (Join-Path $projectRoot $projects[$buildName]),
            '-c', $Configuration,
            '--no-restore',
            '--nologo',
            '-p:NoWarn=NU1801'
        )
}

$brokerExecutable = Join-Path $projectRoot `
    "driver\user\KeyPilot.DriverBroker\bin\$Configuration\net10.0\win-x64\KeyPilot.DriverBroker.exe"
if (Test-Path -LiteralPath $brokerExecutable -PathType Leaf) {
    $brokerHash = (Get-FileHash -LiteralPath $brokerExecutable -Algorithm SHA256).Hash
    Write-Host "Broker SHA-256 (packaging input only): $brokerHash"
    Write-Host 'Do not elevate this development-tree binary. Production launch requires a protected Program Files installation and a trusted expected hash.'
}

if ($BuildDriver) {
    Invoke-PowerShellStep `
        -Description 'Build KMDF driver (no installation or signing)' `
        -ScriptPath (Join-Path $projectRoot 'driver\scripts\Build-Driver.ps1') `
        -Arguments @($Configuration, 'x64')
}

Write-Host "KeyPilot $Configuration build and tests completed successfully. No driver or broker was installed or started."
