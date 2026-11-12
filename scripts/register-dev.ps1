# Packaged debug run: registers the app (the WinUI 3 build) as a loose-file MSIX package so it gets
# package identity (needed for app-notification access and the MSIX startup task), then launches it.
# Requires Windows "Developer Mode" (Settings > System > For developers). No certificate needed.
#   .\scripts\register-dev.ps1             build, register (or update the registration) and launch
#   .\scripts\register-dev.ps1 -Unregister remove the dev package (its data is backed up first)
#
# The package keeps its own data (settings, the encrypted Claude key) in its private storage, and
# removing the package deletes that storage. So a normal run never unregisters: it re-registers over
# the existing package (same version from the same folder, or a higher Version in the manifest).
param([switch]$Unregister)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$existing = Get-AppxPackage -Name DynamicNotch -ErrorAction SilentlyContinue
if ($Unregister) {
    if (-not $existing) { Write-Host "Not registered."; return }
    $data = Join-Path $env:LOCALAPPDATA "Packages\$($existing.PackageFamilyName)\LocalCache\Local\DynamicNotch"
    if (Test-Path $data) {
        $backup = Join-Path $root 'artifacts\package-data-backup'
        New-Item -ItemType Directory -Force $backup | Out-Null
        Copy-Item (Join-Path $data '*') $backup -Recurse -Force
        Write-Host "Backed up the package's data to $backup"
    }
    Remove-AppxPackage $existing.PackageFullName
    Write-Host "Unregistered."
    return
}

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$layout = Join-Path $root 'artifacts\package-layout'
$proj = Join-Path $root 'src\DynamicNotch.WinUI\DynamicNotch.WinUI.csproj'

Get-Process DynamicNotch -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $layout | Out-Null

# Self-contained: the Windows App SDK ships inside the package, nothing to install separately.
dotnet publish $proj -c Debug -r "win-$arch" -p:Platform=$arch --self-contained true -o $layout -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

New-Item -ItemType Directory -Force (Join-Path $layout 'Assets') | Out-Null
Copy-Item (Join-Path $root 'Package\Assets\*') (Join-Path $layout 'Assets') -Recurse -Force
# A packaged process reads its XAML resources from resources.pri at the package root.
Copy-Item (Join-Path $layout 'DynamicNotch.pri') (Join-Path $layout 'resources.pri') -Force
(Get-Content (Join-Path $root 'Package\AppxManifest.xml') -Raw).Replace('__ARCH__', $arch) |
    Set-Content (Join-Path $layout 'AppxManifest.xml') -Encoding utf8

try {
    Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml') -ForceApplicationShutdown
} catch {
    Write-Warning "Registration failed. Is Developer Mode on? (Settings > System > For developers)"
    Write-Warning "If a package with the same Version is registered from another folder, raise Version in Package\AppxManifest.xml."
    throw
}

$pkg = Get-AppxPackage -Name DynamicNotch
$aumid = "$($pkg.PackageFamilyName)!App"
Write-Host "Registered $($pkg.PackageFullName) from $($pkg.InstallLocation). Launching $aumid"
# Launching through the AUMID is what gives the process its package identity.
Start-Process "shell:AppsFolder\$aumid"
