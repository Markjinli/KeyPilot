[CmdletBinding()]
param(
    [string]$ProjectRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
}
$xmlExtensions = @('.xaml', '.csproj', '.vcxproj', '.filters', '.props', '.manifest')
$textExtensions = @(
    '.cs', '.c', '.h', '.xaml', '.csproj', '.vcxproj', '.filters', '.props',
    '.manifest', '.md', '.ps1', '.cmd', '.json', '.inf'
)
$utf8 = New-Object System.Text.UTF8Encoding($false, $true)
$errors = New-Object System.Collections.Generic.List[string]
$checkedXml = 0
$checkedUtf8 = 0

Get-ChildItem -LiteralPath $ProjectRoot -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj|artifacts|\.dotnet-home|\.git)[\\/]' -and
        $_.FullName -notmatch '[\\/]driver[\\/]KeyPilotFilter[\\/](x64|ARM64)[\\/]'
    } |
    ForEach-Object {
        if ($xmlExtensions -contains $_.Extension) {
            try {
                $document = New-Object System.Xml.XmlDocument
                $document.PreserveWhitespace = $true
                $document.Load($_.FullName)
                $checkedXml++
            }
            catch {
                $errors.Add("XML 无效：$($_.FullName) — $($_.Exception.Message)")
            }
        }

        if ($textExtensions -contains $_.Extension) {
            try {
                $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
                [void]$utf8.GetString($bytes)
                $checkedUtf8++
            }
            catch {
                $errors.Add("UTF-8 无效：$($_.FullName) — $($_.Exception.Message)")
            }
        }
    }

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Source verification passed: $checkedXml XML files, $checkedUtf8 UTF-8 text files."
