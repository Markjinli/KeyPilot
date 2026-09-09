[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
if ([string]::IsNullOrWhiteSpace($programFiles)) { throw 'Windows Program Files directory was not found.' }
$destination = [IO.Path]::GetFullPath((Join-Path $programFiles 'KeyPilot'))
$programFilesRoot = [IO.Path]::GetFullPath($programFiles) + [IO.Path]::DirectorySeparatorChar
if (-not $destination.StartsWith($programFilesRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($destination) -cne 'KeyPilot') {
    throw "Refusing an unexpected installation target: $destination"
}
foreach ($path in @($source, $destination)) {
    if (Test-Path -LiteralPath $path) {
        $item = Get-Item -LiteralPath $path -Force
        $points = @($item)
        if ($item.PSIsContainer) { $points += @(Get-ChildItem -LiteralPath $path -Force -Recurse) }
        $points = @($points | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
        if ($points.Count -ne 0) {
            throw "Refusing an installation source/target that contains a link or reparse point: $($points.FullName -join ', ')"
        }
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This installer must run as administrator. Use “安装 KeyPilot.cmd” to request UAC.'
}

function Assert-Package([string]$Root) {
    $verify = Join-Path $Root 'Verify-Release.ps1'
    if (-not (Test-Path -LiteralPath $verify -PathType Leaf)) { throw "Release verifier is missing: $verify" }
    & $verify -PackageDirectory $Root
    if (-not $?) { throw "Release verification failed: $Root" }
}

function Assert-ProtectedAcl([string]$Root) {
    $unsafeSids = @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545')
    $writeMask = [Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    $items = @((Get-Item -LiteralPath $Root))
    $items += @(Get-ChildItem -LiteralPath $Root -Force -Recurse)
    foreach ($item in $items) {
        $acl = Get-Acl -LiteralPath $item.FullName
        $accessRules = $acl.GetAccessRules(
            $true,
            $true,
            [Security.Principal.SecurityIdentifier])
        foreach ($rule in $accessRules) {
            if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
            $sid = $rule.IdentityReference.Value
            if ($unsafeSids -contains $sid -and (($rule.FileSystemRights -band $writeMask) -ne 0)) {
                throw "Unsafe writable ACL for $sid on $($item.FullName): $($rule.FileSystemRights)"
            }
        }
    }
}

function Test-PathInsideRoot([string]$Root, [string]$Candidate) {
    $separator = [IO.Path]::DirectorySeparatorChar
    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + $separator
    $normalizedCandidate = [IO.Path]::GetFullPath($Candidate).Replace(
        [IO.Path]::AltDirectorySeparatorChar, $separator)
    return $normalizedCandidate.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)
}

function Get-InstalledKeyPilotProcesses([string]$Root) {
    @(Get-Process -Name 'KeyPilot.App', 'KeyPilot.DriverBroker' -ErrorAction SilentlyContinue | Where-Object {
        try {
            $path = $_.MainModule.FileName
            Test-PathInsideRoot $Root $path
        } catch { $false }
    })
}

Assert-Package $source
if ($source.Equals($destination, [StringComparison]::OrdinalIgnoreCase)) {
    Assert-ProtectedAcl $destination
    Write-Host "KeyPilot is already installed and verified: $destination"
    exit 0
}

$running = @(Get-InstalledKeyPilotProcesses $destination)
if ($running.Count -ne 0) {
    throw 'Close the installed KeyPilot App/Broker before updating, then run the installer again.'
}

$suffix = [Guid]::NewGuid().ToString('N')
$staging = Join-Path $programFiles ".KeyPilot.installing-$suffix"
$backup = Join-Path $programFiles ".KeyPilot.previous-$suffix"
$promoted = $false
try {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $staging -Recurse -Force
    Assert-Package $staging

    $icacls = Join-Path ([Environment]::SystemDirectory) 'icacls.exe'
    if (-not (Test-Path -LiteralPath $icacls -PathType Leaf)) {
        throw "Windows ACL utility was not found: $icacls"
    }
    & $icacls $staging /inheritance:e /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not enable protected Program Files ACL inheritance ($LASTEXITCODE)." }
    & $icacls $staging /reset /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not reset installation ACLs ($LASTEXITCODE)." }
    Assert-ProtectedAcl $staging

    if (Test-Path -LiteralPath $destination) { Move-Item -LiteralPath $destination -Destination $backup }
    Move-Item -LiteralPath $staging -Destination $destination
    $promoted = $true
    Assert-Package $destination
    Assert-ProtectedAcl $destination

    $commonApplicationData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonApplicationData)
    if ([string]::IsNullOrWhiteSpace($commonApplicationData)) {
        throw 'Windows CommonApplicationData directory was not found.'
    }
    $shortcutDirectory = Join-Path $commonApplicationData 'Microsoft\Windows\Start Menu\Programs'
    $shortcutPath = Join-Path $shortcutDirectory 'KeyPilot.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $installedAppPath = Join-Path $destination 'KeyPilot.App.exe'
    $shortcut.TargetPath = $installedAppPath
    $shortcut.WorkingDirectory = $destination
    $shortcut.Description = 'KeyPilot 按键采集与映射'
    $shortcut.IconLocation = "$installedAppPath,0"
    $shortcut.Save()

    if (Test-Path -LiteralPath $backup) {
        try { Remove-Item -LiteralPath $backup -Recurse -Force }
        catch { Write-Warning "The new installation is valid, but the previous backup could not be removed: $backup" }
    }
    Write-Host "KeyPilot App/Broker installed and verified: $destination"
    Write-Host 'A Start Menu shortcut named KeyPilot was created.'
    Write-Warning 'This installer did not install the keyboard driver or change Secure Boot, TESTSIGNING, BCD, or certificate stores.'
} catch {
    $originalFailure = $_
    if ($promoted -and (Test-Path -LiteralPath $destination)) {
        try { Remove-Item -LiteralPath $destination -Recurse -Force }
        catch {
            Write-Warning "Automatic rollback could not remove the new directory. The previous installation was preserved at: $backup"
            throw $originalFailure
        }
    }
    if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $destination)) {
        Move-Item -LiteralPath $backup -Destination $destination
    }
    throw $originalFailure
} finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
