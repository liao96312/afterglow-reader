$ErrorActionPreference = 'Stop'
$source = Split-Path -Parent $MyInvocation.MyCommand.Path
$target = Join-Path $env:LOCALAPPDATA 'AfterglowReader'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'

New-Item -ItemType Directory -Force -Path $target, $startMenu | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $startMenu 'AfterglowReader.lnk'))
$shortcut.TargetPath = Join-Path $target 'AfterglowReader.exe'
$shortcut.WorkingDirectory = $target
$shortcut.IconLocation = Join-Path $target 'AfterglowReader.exe'
$shortcut.Save()

Write-Host "Afterglow Reader installed to $target"
Write-Host 'Start Menu shortcut created.'
