$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'AfterglowReader'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\AfterglowReader.lnk'

if (Test-Path -LiteralPath $shortcut) {
    Remove-Item -LiteralPath $shortcut -Force
}
if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Recurse -Force
}

Write-Host 'Afterglow Reader uninstalled.'
