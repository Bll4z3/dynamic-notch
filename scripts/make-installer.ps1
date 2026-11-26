# Builds DynamicNotch-Setup-v<version>.exe: one per-user setup for x64 and Arm64 PCs (installer\DynamicNotch.iss).
# Needs Inno Setup 6.3+:
#     winget install --id JRSoftware.InnoSetup -e --scope user
#
#   .\scripts\make-installer.ps1
#       publishes win-x64 and win-arm64 (self-contained, Release) from source, then builds the setup
#   .\scripts\make-installer.ps1 -X64Zip DynamicNotch-v2.11.0-win-x64.zip -Arm64Zip DynamicNotch-v2.11.0-win-ARM64.zip
#       packages the exact files of existing release zips instead (the version is read from the exe)
#   -X64Dir / -Arm64Dir   use publish folders you already have
#
# The setup lands in artifacts\installer. Release it next to the zips: the in-app updater still downloads the
# "win-<arch>" zip, and installed copies update in place like any other.
param([string]$X64Dir, [string]$Arm64Dir, [string]$X64Zip, [string]$Arm64Zip, [string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root 'artifacts\installer-input'
$out = Join-Path $root 'artifacts\installer'
$project = Join-Path $root 'src\DynamicNotch.WinUI\DynamicNotch.WinUI.csproj'

function Find-Iscc {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($base in @("$env:LOCALAPPDATA\Programs", ${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if (-not $base) { continue }
        $p = Join-Path $base 'Inno Setup 6\ISCC.exe'
        if (Test-Path $p) { return $p }
    }
    throw 'ISCC.exe not found. Install Inno Setup: winget install --id JRSoftware.InnoSetup -e --scope user'
}

function Expand-Input([string]$zip, [string]$name) {
    $dir = Join-Path $work $name
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    Expand-Archive $zip $dir
    $dir
}

function Publish-Arch([string]$arch) {
    $dir = Join-Path $work $arch
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    $platform = if ($arch -eq 'arm64') { 'ARM64' } else { 'x64' }
    dotnet publish $project -c Release -p:Platform=$platform -r "win-$arch" --self-contained true -o $dir -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($arch)" }
    $dir
}

New-Item -ItemType Directory -Force $work, $out | Out-Null
if ($X64Zip) { $X64Dir = Expand-Input (Resolve-Path $X64Zip) 'x64' }
if ($Arm64Zip) { $Arm64Dir = Expand-Input (Resolve-Path $Arm64Zip) 'arm64' }
if (-not $X64Dir) { $X64Dir = Publish-Arch 'x64' }
if (-not $Arm64Dir) { $Arm64Dir = Publish-Arch 'arm64' }
$X64Dir = (Resolve-Path $X64Dir).Path
$Arm64Dir = (Resolve-Path $Arm64Dir).Path

foreach ($d in $X64Dir, $Arm64Dir) {
    if (-not (Test-Path (Join-Path $d 'DynamicNotch.exe'))) { throw "DynamicNotch.exe not found in $d" }
}
if (-not $Version) {
    # ProductVersion is "2.11.0+<commit>"; keep the x.y.z part.
    $Version = ((Get-Item (Join-Path $X64Dir 'DynamicNotch.exe')).VersionInfo.ProductVersion -split '[+-]')[0]
}
$armVersion = ((Get-Item (Join-Path $Arm64Dir 'DynamicNotch.exe')).VersionInfo.ProductVersion -split '[+-]')[0]
if ($armVersion -ne $Version) { throw "Version mismatch: x64 is $Version, Arm64 is $armVersion" }

$iscc = Find-Iscc
& $iscc /Q "/DAppVersion=$Version" "/DX64Dir=$X64Dir" "/DArm64Dir=$Arm64Dir" "/DOutputDir=$out" (Join-Path $root 'installer\DynamicNotch.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }

$setup = Get-Item (Join-Path $out "DynamicNotch-Setup-v$Version.exe")
'{0} ({1:N1} MB)' -f $setup.FullName, ($setup.Length / 1MB)
