[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Root
)

$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath(('' + $Root).Trim().TrimEnd('\', '/'))
if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
    return
}

$keep = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[void]$keep.Add('en-US')
[void]$keep.Add('zh-CN')

Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    $mui = Join-Path $_.FullName 'Microsoft.ui.xaml.dll.mui'
    if (-not (Test-Path -LiteralPath $mui -PathType Leaf)) {
        return
    }

    if (-not $keep.Contains($_.Name)) {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }
}
