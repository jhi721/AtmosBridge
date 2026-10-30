; AtmosBridge per-user installer (Inno Setup 7). Build: ISCC /DMyAppVersion=1.2.3 installer\AtmosBridge.iss
; Expects the self-contained publish in ..\publish (see .github/workflows/build.yml).

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef Compression
  #define Compression "lzma2/max"
#endif
; 2 LZMA2 block threads: the sweet spot on the 4-core CI runner (4 were not faster)
#ifndef BlockThreads
  #define BlockThreads "2"
#endif
#define Exe "AtmosBridge.exe"

[Setup]
AppId={{25C5F212-A339-413A-8EFD-76FEDCEBFAA9}
AppName=AtmosBridge
AppVersion={#MyAppVersion}
AppPublisher=jhi721
AppPublisherURL=https://github.com/jhi721/AtmosBridge
DefaultDirName={localappdata}\Programs\AtmosBridge
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
; 64-bit Setup: ArchitecturesAllowed and ArchitecturesInstallIn64BitMode default to x64compatible
SetupArchitecture=x64
MinVersion=10.0.17763
OutputDir=..\out
OutputBaseFilename=AtmosBridge-{#MyAppVersion}-setup
Compression={#Compression}
LZMANumBlockThreads={#BlockThreads}
SolidCompression=yes
; Follows the Windows light/dark setting; dark pages take the icons' background
WizardStyle=modern dynamic includetitlebar hidebevels
WizardBackColorDynamicDark=#1C1A19
; Setup picks the file closest to the DPI-scaled image area (icons\make_icons.py makes them)
; Dark mode takes the DynamicDark images (built-in ones when unset): same files for both
WizardImageFile=..\icons\sidebar-*.png
WizardImageFileDynamicDark=..\icons\sidebar-*.png
WizardSmallImageFile=..\icons\header-*.png
WizardSmallImageFileDynamicDark=..\icons\header-*.png
; Rounded corners stay transparent (custom images default to the window color)
WizardSmallImageBackColor=none
WizardSmallImageBackColorDynamicDark=none
; The sidebar shows on the welcome and finish pages only
DisableWelcomePage=no
SetupIconFile=..\icons\atmosbridge.ico
UninstallDisplayIcon={app}\{#Exe}
UninstallDisplayName=AtmosBridge

[Tasks]
Name: autostart; Description: "Start AtmosBridge when I sign in to Windows"
Name: mpvnet; Description: "Configure mpv.net (adds an AtmosBridge block to %APPDATA%\mpv.net\mpv.conf)"; Check: HasMpvNet
Name: mpv; Description: "Configure mpv (adds an AtmosBridge block to %APPDATA%\mpv\mpv.conf)"; Check: HasMpv; Flags: unchecked

[Files]
Source: "..\publish\*"; Excludes: "*.pdb"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "mpvconf.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "atmosbridge.lua"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userstartup}\AtmosBridge"; Filename: "{app}\{#Exe}"; Parameters: "--log ""{localappdata}\AtmosBridge\log.txt"""; Tasks: autostart
Name: "{userprograms}\AtmosBridge\AtmosBridge"; Filename: "{app}\{#Exe}"; Parameters: "--log ""{localappdata}\AtmosBridge\log.txt"""
Name: "{userprograms}\AtmosBridge\AtmosBridge log"; Filename: "{localappdata}\AtmosBridge\log.txt"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\mpvconf.ps1"" -Action add -Path ""{userappdata}\mpv.net\mpv.conf"""; Flags: runhidden; Tasks: mpvnet
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\mpvconf.ps1"" -Action add -Path ""{userappdata}\mpv\mpv.conf"""; Flags: runhidden; Tasks: mpv
Filename: "{app}\{#Exe}"; Parameters: "--log ""{localappdata}\AtmosBridge\log.txt"""; Description: "Start AtmosBridge now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /t /im {#Exe}"; Flags: runhidden; RunOnceId: "StopBridge"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\mpvconf.ps1"" -Action remove -Path ""{userappdata}\mpv.net\mpv.conf"""; Flags: runhidden; RunOnceId: "ConfMpvNet"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\mpvconf.ps1"" -Action remove -Path ""{userappdata}\mpv\mpv.conf"""; Flags: runhidden; RunOnceId: "ConfMpv"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\AtmosBridge"

[Code]
function HasMpvNet: Boolean;
begin
  Result := DirExists(ExpandConstant('{userappdata}\mpv.net'));
end;

function HasMpv: Boolean;
begin
  Result := DirExists(ExpandConstant('{userappdata}\mpv'));
end;

// Upgrades: the running bridge (and its thd-objects / dtsx-objects child) lock the files being replaced
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /t /im {#Exe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
