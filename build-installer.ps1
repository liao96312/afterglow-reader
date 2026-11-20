# Builds the self-contained reader plus a single-file installer.
#   powershell -ExecutionPolicy Bypass -File build-installer.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$appProject = Join-Path $root 'src\AfterglowReader\AfterglowReader.csproj'
$setupProject = Join-Path $root 'src\AfterglowReader.Setup\AfterglowReader.Setup.csproj'
$publishDir = Join-Path $root 'artifacts\publish'
$payloadZip = Join-Path $root 'src\AfterglowReader.Setup\payload.zip'
$installerOut = Join-Path $root 'artifacts\installer'
$version = ((Get-Content (Join-Path $root 'src\AfterglowReader.Setup\AfterglowReader.Setup.csproj')) |
    Select-String '<Version>').Line -replace '.*<Version>|</Version>.*', ''
$installerExe = Join-Path $root "artifacts\AfterglowReader-Setup-$version.exe"

Write-Host '==> 1/4 Running tests' -ForegroundColor Cyan
& $dotnet test (Join-Path $root 'AfterglowReader.slnx') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'tests failed' }

Write-Host '==> 2/4 Publishing reader' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
& $dotnet publish $appProject -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Write-Host '==> 3/4 Packing payload' -ForegroundColor Cyan
if (Test-Path $payloadZip) { Remove-Item -Force $payloadZip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publishDir,
    $payloadZip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
Write-Host ("    payload: {0:N1} MB" -f ((Get-Item $payloadZip).Length / 1MB))

Write-Host '==> 4/4 Building installer' -ForegroundColor Cyan
if (Test-Path $installerOut) { Remove-Item -Recurse -Force $installerOut }
& $dotnet publish $setupProject -c Release -r win-x64 --self-contained true -o $installerOut
if ($LASTEXITCODE -ne 0) { throw 'installer build failed' }

$built = Join-Path $installerOut 'AfterglowReader.Setup.exe'
if (-not (Test-Path $built)) { throw "setup output missing: $built" }
if ((Get-Item $built).Length -lt 20MB) { throw 'installer is too small - payload was not embedded' }
Copy-Item $built $installerExe -Force
Remove-Item -Force $payloadZip

Write-Host ''
Write-Host ("Done: {0} ({1:N1} MB)" -f $installerExe, ((Get-Item $installerExe).Length / 1MB)) -ForegroundColor Green
