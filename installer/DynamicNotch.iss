; Dynamic Notch setup (Inno Setup 6.3+). Build it with scripts\make-installer.ps1, which passes:
;   /DAppVersion=2.11.0  /DX64Dir=<win-x64 publish folder>  /DArm64Dir=<win-arm64 publish folder>  /DOutputDir=<folder>
;
; One per-user setup for both architectures: no admin prompt, installs to %LocalAppData%\Programs\DynamicNotch
; (the folder the in-app updater then updates in place), adds a Start menu entry and an uninstall entry, and can
; turn on Start with Windows (the same HKCU Run value the app's own toggle uses). Settings live in
; %LocalAppData%\DynamicNotch and are kept on uninstall.

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z (scripts\make-installer.ps1 does this)
#endif
#ifndef X64Dir
  #error Pass /DX64Dir=<win-x64 publish folder>
#endif
#ifndef Arm64Dir
  #error Pass /DArm64Dir=<win-arm64 publish folder>
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "Dynamic Notch"
#define AppExe "DynamicNotch.exe"
#define RepoUrl "https://github.com/Bll4z3/dynamic-notch"

[Setup]
; Never change AppId: it ties upgrades and the uninstall entry together.
AppId={{B387B9C9-A34C-47BD-91BE-32545EDE5C96}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Bll4z3
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\DynamicNotch
; Fixed folder: the uninstaller removes it whole (files the in-app updater added included), so it must never be a
; folder the user picked that holds other things.
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=DynamicNotch-Setup-v{#AppVersion}
SetupIconFile=..\src\DynamicNotch.WinUI\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
; The app is stopped in [Code] instead (it lives in the tray, so Restart Manager can't close it cleanly).
CloseApplications=no
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
; Arm64 PCs get the native Arm64 build; every other PC gets x64. solidbreak keeps the two in separate compressed
; blocks, so an x64 PC never has to unpack the Arm64 files (and vice versa).
Source: "{#X64Dir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not IsArm64
Source: "{#Arm64Dir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs solidbreak; Check: IsArm64

[Icons]
; Same name as the shortcut the portable build creates for itself (StartMenuService), so there is never a duplicate.
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DynamicNotch"; \
    ValueData: """{app}\{#AppExe}"""; Tasks: startup

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

{ One notch per session: stop any running copy so its files can be replaced and the new one can start. }
procedure StopApp();
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if Code = 0 then Sleep(800);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopApp();
  Result := True;
end;

{ The app's own Start with Windows toggle may have written the Run value too: remove it when it points here. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if CurUninstallStep = usPostUninstall then
    if RegQueryStringValue(HKCU, RunKey, 'DynamicNotch', Value) then
      if Pos(Lowercase(ExpandConstant('{app}\')), Lowercase(Value)) > 0 then
        RegDeleteValue(HKCU, RunKey, 'DynamicNotch');
end;
