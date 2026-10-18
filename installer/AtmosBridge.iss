; AtmosBridge per-user installer (Inno Setup 6). Build: ISCC /DMyAppVersion=1.2.3 installer\AtmosBridge.iss
; Expects the self-contained publish in ..\publish (see .github/workflows/build.yml).

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#define Exe "AtmosBridge.exe"
#define LogArg "--log ""{localappdata}\AtmosBridge\log.txt"""
#define ConfScript "-NoProfile -ExecutionPolicy Bypass -File ""{app}\mpvconf.ps1"""

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
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\out
OutputBaseFilename=AtmosBridge-{#MyAppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#Exe}
UninstallDisplayName=AtmosBridge

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.Autostart=Start AtmosBridge when I sign in to Windows
ru.Autostart=Запускать AtmosBridge при входе в Windows
en.ConfMpvNet=Configure mpv.net (adds an AtmosBridge block to %APPDATA%\mpv.net\mpv.conf)
ru.ConfMpvNet=Настроить mpv.net (добавить блок AtmosBridge в %APPDATA%\mpv.net\mpv.conf)
en.ConfMpv=Configure mpv (adds an AtmosBridge block to %APPDATA%\mpv\mpv.conf)
ru.ConfMpv=Настроить mpv (добавить блок AtmosBridge в %APPDATA%\mpv\mpv.conf)
en.StartNow=Start AtmosBridge now
ru.StartNow=Запустить AtmosBridge сейчас
en.Log=AtmosBridge log
ru.Log=Лог AtmosBridge

[Tasks]
Name: autostart; Description: "{cm:Autostart}"
Name: mpvnet; Description: "{cm:ConfMpvNet}"; Check: HasMpvNet
Name: mpv; Description: "{cm:ConfMpv}"; Check: HasMpv; Flags: unchecked

[Files]
Source: "..\publish\*"; Excludes: "*.pdb"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "mpvconf.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userstartup}\AtmosBridge"; Filename: "{app}\{#Exe}"; Parameters: "{#LogArg}"; Tasks: autostart
Name: "{userprograms}\AtmosBridge\AtmosBridge"; Filename: "{app}\{#Exe}"; Parameters: "{#LogArg}"
Name: "{userprograms}\AtmosBridge\{cm:Log}"; Filename: "{localappdata}\AtmosBridge\log.txt"

[Run]
Filename: "powershell.exe"; Parameters: "{#ConfScript} -Action add -Path ""{userappdata}\mpv.net\mpv.conf"""; Flags: runhidden; Tasks: mpvnet
Filename: "powershell.exe"; Parameters: "{#ConfScript} -Action add -Path ""{userappdata}\mpv\mpv.conf"""; Flags: runhidden; Tasks: mpv
Filename: "{app}\{#Exe}"; Parameters: "{#LogArg}"; Description: "{cm:StartNow}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /t /im {#Exe}"; Flags: runhidden; RunOnceId: "StopBridge"
Filename: "powershell.exe"; Parameters: "{#ConfScript} -Action remove -Path ""{userappdata}\mpv.net\mpv.conf"""; Flags: runhidden; RunOnceId: "ConfMpvNet"
Filename: "powershell.exe"; Parameters: "{#ConfScript} -Action remove -Path ""{userappdata}\mpv\mpv.conf"""; Flags: runhidden; RunOnceId: "ConfMpv"

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

// Upgrades: the running bridge (and its thd-objects child) lock the files being replaced
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /t /im {#Exe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
