# Debug run profile: builds and starts the app (WinUI 3) WITHOUT package identity.
# Everything works except app-notification pills and "Start with Windows" (those need
# register-dev.ps1 or the MSIX). It doesn't see the packaged app's data (settings, Claude key).
#   .\scripts\run-debug.ps1          normal run
#   .\scripts\run-debug.ps1 -Demo    also queues one sample pill of each kind and sample clips
param([switch]$Demo)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\DynamicNotch.WinUI\DynamicNotch.WinUI.csproj'
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }

Get-Process DynamicNotch -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build $proj -c Debug -p:Platform=$arch -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$rid = if ($arch -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$exe = Join-Path $root "src\DynamicNotch.WinUI\bin\$arch\Debug\net8.0-windows10.0.19041.0\$rid\DynamicNotch.exe"
if ($Demo) { Start-Process $exe -ArgumentList '--demo' } else { Start-Process $exe }
Write-Host "Dynamic Notch is running. Right-click its tray icon to quit."
