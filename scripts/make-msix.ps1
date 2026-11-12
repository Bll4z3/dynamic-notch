# Builds a signed, self-contained .msix of the WinUI 3 app you can install by double-clicking.
# Needs the Windows SDK for makeappx/signtool:
#     winget install Microsoft.WindowsSDK.10.0.22621
#
#   .\scripts\make-msix.ps1                 build + sign with a self-signed dev certificate
#   .\scripts\make-msix.ps1 -TrustCert      also trust that certificate on this PC (asks for admin)
#
# Self-signed packages only install where the certificate is trusted. For distribution, sign with a
# real code-signing certificate (set -PfxPath / -PfxPassword) and match Publisher in AppxManifest.xml.
param([switch]$TrustCert, [string]$PfxPath, [string]$PfxPassword = 'dynamicnotch')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$out = Join-Path $root 'artifacts'
$layout = Join-Path $out "msix-layout-$arch"
$version = ([xml](Get-Content (Join-Path $root 'Package\AppxManifest.xml'))).Package.Identity.Version
$msix = Join-Path $out "DynamicNotch_${version}_$arch.msix"
$publisher = 'CN=DynamicNotch Dev'

function Find-SdkTool($name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
    $hit = Get-ChildItem $kits -Recurse -Filter "$name.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $hit) { throw "$name.exe not found. Install the Windows SDK: winget install Microsoft.WindowsSDK.10.0.22621" }
    $hit.FullName
}
$makeappx = Find-SdkTool 'makeappx'
$signtool = Find-SdkTool 'signtool'

if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

dotnet publish (Join-Path $root 'src\DynamicNotch.WinUI\DynamicNotch.WinUI.csproj') -c Release -r "win-$arch" -p:Platform=$arch `
    --self-contained true -p:PublishReadyToRun=true -o $layout -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

New-Item -ItemType Directory -Force (Join-Path $layout 'Assets') | Out-Null
Copy-Item (Join-Path $root 'Package\Assets\*') (Join-Path $layout 'Assets') -Recurse -Force
# A packaged process reads its XAML resources from resources.pri at the package root.
Copy-Item (Join-Path $layout 'DynamicNotch.pri') (Join-Path $layout 'resources.pri') -Force
(Get-Content (Join-Path $root 'Package\AppxManifest.xml') -Raw).Replace('__ARCH__', $arch) |
    Set-Content (Join-Path $layout 'AppxManifest.xml') -Encoding utf8

& $makeappx pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }

if (-not $PfxPath) {
    $PfxPath = Join-Path $out 'DynamicNotchDev.pfx'
    $cerPath = Join-Path $out 'DynamicNotchDev.cer'
    if (-not (Test-Path $PfxPath)) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
            -FriendlyName 'Dynamic Notch dev signing' -CertStoreLocation 'Cert:\CurrentUser\My' `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        $pwd = ConvertTo-SecureString $PfxPassword -AsPlainText -Force
        Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $pwd | Out-Null
        Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
        Write-Host "Created dev certificate $cerPath"
    }
    if ($TrustCert) {
        Write-Host "Trusting $cerPath (LocalMachine\TrustedPeople) - an admin prompt will appear."
        Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -Command Import-Certificate -FilePath '$cerPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    }
}

& $signtool sign /fd SHA256 /a /f $PfxPath /p $PfxPassword $msix
if ($LASTEXITCODE -ne 0) { throw "signtool failed" }
Write-Host "`nBuilt $msix"
Write-Host "Install: double-click it (the signing certificate must be trusted on this PC; use -TrustCert once)."
