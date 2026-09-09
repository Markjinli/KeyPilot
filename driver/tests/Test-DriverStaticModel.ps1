[CmdletBinding()]
param(
    [string]$DriverRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($DriverRoot)) {
    $DriverRoot = Split-Path -Parent $PSScriptRoot
}
$sourcePath = Join-Path $DriverRoot 'KeyPilotFilter\KeyPilotFilter.c'
$headerPath = Join-Path $DriverRoot 'include\KeyPilotProtocol.h'
$infPath = Join-Path $DriverRoot 'KeyPilotFilter\KeyPilotFilter.inf'
$projectPath = Join-Path $DriverRoot 'KeyPilotFilter\KeyPilotFilter.vcxproj'
$buildScriptPath = Join-Path $DriverRoot 'scripts\Build-Driver.ps1'
$preflightPath = Join-Path $DriverRoot 'scripts\Test-InstallPrerequisites.ps1'
$testPreflightPath = Join-Path $DriverRoot 'scripts\Test-TestInstallPrerequisites.ps1'
$testInstallPath = Join-Path $DriverRoot 'scripts\Install-KeyPilotTestDriver.ps1'
$installPath = Join-Path $DriverRoot 'scripts\Install-KeyPilotDriver.ps1'
$uninstallPath = Join-Path $DriverRoot 'scripts\Uninstall-KeyPilotDriver.ps1'
$recoveryPath = Join-Path $DriverRoot 'scripts\Recover-KeyPilotDriver.cmd'

foreach ($path in @($sourcePath, $headerPath, $infPath, $projectPath, $buildScriptPath,
        $preflightPath, $testPreflightPath, $testInstallPath, $installPath, $uninstallPath, $recoveryPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing required driver source: $path"
    }
}

$source = Get-Content -Raw -LiteralPath $sourcePath
$header = Get-Content -Raw -LiteralPath $headerPath
$inf = Get-Content -Raw -LiteralPath $infPath
$project = Get-Content -Raw -LiteralPath $projectPath
$buildScript = Get-Content -Raw -LiteralPath $buildScriptPath
$preflight = Get-Content -Raw -LiteralPath $preflightPath
$testPreflight = Get-Content -Raw -LiteralPath $testPreflightPath
$testInstall = Get-Content -Raw -LiteralPath $testInstallPath
$install = Get-Content -Raw -LiteralPath $installPath
$uninstall = Get-Content -Raw -LiteralPath $uninstallPath
$recovery = Get-Content -Raw -LiteralPath $recoveryPath

$requiredSourceTokens = @(
    'KeyPilotEnterFailOpenLocked',
    'KeyPilotLeaseIsValidLocked',
    'KeyPilotQueueEventLocked',
    'KeyPilotInputRepeat',
    'press->Generation',
    'input->Reserved != 0',
    'KEYBOARD_OVERRUN_MAKE_CODE',
    'KeyPilotLoseTrackingLocked',
    'KeyPilotDriverInputTrackingLost',
    'LastAcknowledgedSequence',
    'LastDeliveredSequence',
    'ProgressDeadline100ns',
    'The inactive buffer is never observed',
    'rule->RuleFlags != KeyPilotRuleSuppressOriginal',
    'KeyPilotRuleHasExactPrefix',
    'EvtDeviceD0Exit',
    'EvtDeviceD0Entry',
    'KeyPilotEvtEmergencyBypassTimer',
    'KEYPILOT_EMERGENCY_HOLD_MILLISECONDS',
    'KeyPilotDriverEmergencyBypassActive',
    'WdfDeviceInitSetExclusive'
)
foreach ($token in $requiredSourceTokens) {
    if ($source.IndexOf($token, [StringComparison]::Ordinal) -lt 0) {
        throw "Fail-open invariant token is missing: $token"
    }
}

$forbiddenKernelTokens = @(
    'ZwCreateProcess',
    'ZwOpenProcess',
    'CreateProcess',
    'ShellExecute',
    'WinExec',
    'http://',
    'https://',
    '.bat',
    '.ps1'
)
foreach ($token in $forbiddenKernelTokens) {
    if ($source.IndexOf($token, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $header.IndexOf($token, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Forbidden kernel capability detected: $token"
    }
}

if ($header.IndexOf('#define KEYPILOT_PROTOCOL_VERSION 2u', [StringComparison]::Ordinal) -lt 0 -or
    $header.IndexOf('KEYPILOT_DEFAULT_LEASE_MILLISECONDS', [StringComparison]::Ordinal) -lt 0 -or
    $header.IndexOf('RuleFlags == KeyPilotRuleSuppressOriginal', [StringComparison]::Ordinal) -lt 0 -or
    $header.IndexOf('RequiredFlags=0,  IgnoredFlags=E0|E1', [StringComparison]::Ordinal) -lt 0 -or
    $header.IndexOf('LastAcknowledgedSequence', [StringComparison]::Ordinal) -lt 0) {
    throw 'Protocol lease or suppression boundary is missing.'
}

if ($inf.IndexOf('StartType=3', [StringComparison]::Ordinal) -lt 0 -or
    $inf.IndexOf('StartType=0', [StringComparison]::Ordinal) -ge 0 -or
    $inf.IndexOf('StartType=1', [StringComparison]::Ordinal) -ge 0) {
    throw 'Driver must remain demand-start during development.'
}
if ($inf.IndexOf('Class=Extension', [StringComparison]::Ordinal) -lt 0 -or
    $inf.IndexOf('AddFilter=KeyPilotFilter', [StringComparison]::Ordinal) -lt 0 -or
    $inf.IndexOf('FilterPosition=Upper', [StringComparison]::Ordinal) -lt 0 -or
    $inf.IndexOf('UpperFilters', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'INF must use an isolated declarative upper-filter extension.'
}

foreach ($token in @(
    'Microsoft.Windows.WDK.x64" Version="10.0.28000.2526',
    'Microsoft.Windows.SDK.cpp.x64" Version="10.0.28000.2526'
)) {
    if ($project.IndexOf($token, [StringComparison]::Ordinal) -lt 0) {
        throw "Pinned x64 WDK/SDK NuGet reference is missing: $token"
    }
}
foreach ($token in @('Component.Microsoft.Windows.DriverKit', 'project.assets.json', '10.0.28000.2526', 'InfVerif /w', 'Get-PeMachine')) {
    if ($buildScript.IndexOf($token, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Pinned build verification is missing: $token"
    }
}
foreach ($token in @(
    '$checks.TestSigningEnabled -eq $true',
    '$checks.SecureBootEnabled -eq $false',
    "`$catalogCoverageEnforcedBy = 'PnPUtil /add-driver'",
    'SignerInLocalMachineTrustedPublisher',
    'IHaveExternalInputAndRecoveryMedia',
    'KEYPILOT-TEST-DRIVER-RISK'
)) {
    if ($testPreflight.IndexOf($token, [StringComparison]::Ordinal) -lt 0) {
        throw "Test-driver fail-closed prerequisite is missing: $token"
    }
}
$mutatingScripts = $testPreflight + "`n" + $testInstall
foreach ($pattern in @('(?i)bcdedit(?:\.exe)?\s+/set', '(?i)Import-Certificate', '(?i)certutil(?:\.exe)?\s+-addstore')) {
    if ($mutatingScripts -match $pattern) {
        throw "Test-driver scripts must not change BCD or certificate stores: $pattern"
    }
}
foreach ($preflightScript in @($preflight, $testPreflight)) {
    if ($preflightScript -match '(?im)^\s*&\s+\$signTool\b' -or
        $preflightScript -match '(?im)^\s*\$signTool\s*=\s*Get-ChildItem\b') {
        throw 'Elevated prerequisite checks must not execute SDK or NuGet signtool binaries.'
    }
    foreach ($token in @('Modules\SecureBoot\SecureBoot.psd1',
            'SecureBoot\Confirm-SecureBootUEFI',
            'Microsoft.PowerShell.Security\Get-AuthenticodeSignature')) {
        if ($preflightScript.IndexOf($token, [StringComparison]::Ordinal) -lt 0) {
            throw "Trusted prerequisite-command qualification is missing: $token"
        }
    }
}

foreach ($recordScript in @($install, $testInstall)) {
    foreach ($token in @('CommonApplicationData', '[IO.FileMode]::CreateNew',
            '[IO.FileOptions]::WriteThrough', '[IO.FileAttributes]::ReparsePoint',
            "'DriverStaging'", '[IO.File]::Copy', 'Dism\Get-WindowsDriver',
            'Microsoft.PowerShell.Utility\Get-FileHash', '$expectedCheckHash',
            '$expectedRecoveryHash', '@(0, 3010)')) {
        if ($recordScript.IndexOf($token, [StringComparison]::Ordinal) -lt 0) {
            throw "Durable recovery-record protection is missing: $token"
        }
    }
    if ($recordScript -match '(?im)^\s*\$checkScript\s*=\s*Join-Path\s+\$PSScriptRoot') {
        throw 'An elevated installer must not execute a helper from its user-writable release directory.'
    }
    if ($recordScript.IndexOf('Microsoft.PowerShell.Management\Get-FileHash',
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw 'Get-FileHash must be qualified to Microsoft.PowerShell.Utility for Windows PowerShell 5.1.'
    }
}

Get-Command 'Microsoft.PowerShell.Utility\Get-FileHash' -ErrorAction Stop | Out-Null
function Assert-EmbeddedCompanionHash(
    [string]$ScriptText,
    [string]$VariableName,
    [string]$ExpectedFile) {
    $pattern = '\$' + [regex]::Escape($VariableName) + "\s*=\s*'([0-9A-Fa-f]{64})'"
    $match = [regex]::Match($ScriptText, $pattern)
    if (-not $match.Success) {
        throw "Embedded companion hash is missing: $VariableName"
    }
    $actual = (Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $ExpectedFile -Algorithm SHA256).Hash
    if (-not [string]::Equals($match.Groups[1].Value, $actual, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Embedded companion hash is stale for $ExpectedFile"
    }
}
Assert-EmbeddedCompanionHash $install 'expectedCheckHash' $preflightPath
Assert-EmbeddedCompanionHash $testInstall 'expectedCheckHash' $testPreflightPath
Assert-EmbeddedCompanionHash $install 'expectedRecoveryHash' $recoveryPath
Assert-EmbeddedCompanionHash $testInstall 'expectedRecoveryHash' $recoveryPath
foreach ($token in @('Get-WindowsDriver -Online -Driver', 'ProviderName -ne ''KeyPilot''',
        'ClassName -ne ''Extension''', '@(0, 3010)')) {
    if ($uninstall.IndexOf($token, [StringComparison]::Ordinal) -lt 0) {
        throw "Uninstall package identity check is missing: $token"
    }
}
foreach ($token in @('KEYPILOT-RECOVERY', '!KEYPILOT_SYSTEM32!config\KeyPilotRecovery-',
        '/r /i /x /c:"Original File Name[ ]*:[ ]*KeyPilotFilter\.inf[ ]*"',
        '/r /i /x /c:"Provider Name[ ]*:[ ]*KeyPilot[ ]*"',
        '/r /i /x /c:"Class Name[ ]*:[ ]*Extension[ ]*"',
        'set "__APPDIR__="', 'set "RANDOM="',
        'set /p "KEYPILOT_PUBLISHED_NAME=', 'setlocal EnableDelayedExpansion',
        '"3010"')) {
    if ($recovery.IndexOf($token, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "WinRE recovery guard is missing: $token"
    }
}
if ([regex]::Matches($recovery, '/English', [Text.RegularExpressions.RegexOptions]::IgnoreCase).Count -ne 2) {
    throw 'Online and offline DISM identity inspection must both force stable English field labels.'
}
if ($recovery -match '%(?:~?[0-9]|\*)') {
    throw 'WinRE recovery must never expand command-line arguments inside CMD syntax.'
}
if ($recovery -match '(?i)%(?:SystemRoot|RANDOM|__APPDIR__)%') {
    throw 'WinRE recovery must not expand caller-controlled environment variables.'
}
if ($recovery.IndexOf('%TEMP%\KeyPilotDriverInfo-', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'WinRE recovery must not use a predictable file in the caller-controlled TEMP directory.'
}

Write-Host 'Driver static model checks passed.'
