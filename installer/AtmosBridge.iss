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
#define MpvDefaultDir "{userappdata}\mpv"
#define AppGuid "{25C5F212-A339-413A-8EFD-76FEDCEBFAA9}"

[Setup]
AppId={{#AppGuid}
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
; mpv git-release builds (github.com/mpv-player/mpv/releases/tag/git-release) read %APPDATA%\mpv\mpv.conf; the next
; page lets the user pick another config folder (portable_config, ...)
Name: mpv; Description: "Configure mpv (adds an AtmosBridge block to mpv.conf and input.conf)"

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
; mpv's config: CurStepChanged
Filename: "{app}\{#Exe}"; Parameters: "--log ""{localappdata}\AtmosBridge\log.txt"""; Description: "Start AtmosBridge now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /t /im {#Exe}"; Flags: runhidden; RunOnceId: "StopBridge"
; Installs from before mpv.net was dropped could have configured it too
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\mpvconf.ps1"" -Action remove -Path ""{userappdata}\mpv.net\mpv.conf"""; Flags: runhidden; RunOnceId: "ConfMpvNet"
; The mpv config folder chosen at install: CurUninstallStepChanged

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\AtmosBridge"

[Code]
// Upgrades: the running bridge (and its thd-objects / dtsx-objects child) lock the files being replaced
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /t /im {#Exe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;

// mpv config folder (mpv.conf, input.conf, scripts\), asked only with the mpv task. Kept for the next install and the
// uninstall as Inno's previous data ("Inno Setup CodeFile: MpvDir" in the uninstall key).
var
  MpvDirPage: TInputDirWizardPage;
  OldMpvDir: String; // folder the installed version configured, '' on a first install

const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppGuid}_is1';

procedure InitializeWizard;
begin
  // Installs from before the folder choice configured %APPDATA%\mpv
  if RegKeyExists(HKCU, UninstallKey) then
    OldMpvDir := GetPreviousData('MpvDir', ExpandConstant('{#MpvDefaultDir}'));
  MpvDirPage := CreateInputDirPage(wpSelectTasks, 'mpv configuration', 'Which folder does mpv read mpv.conf from?',
    'AtmosBridge adds its settings to mpv.conf and input.conf there and its script to scripts\. ' +
    'Usually %APPDATA%\mpv; a portable mpv reads portable_config next to mpv.exe.', False, '');
  MpvDirPage.Add('');
  if OldMpvDir <> '' then
    MpvDirPage.Values[0] := OldMpvDir
  else
    MpvDirPage.Values[0] := ExpandConstant('{#MpvDefaultDir}');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = MpvDirPage.ID) and not WizardIsTaskSelected('mpv');
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  // Without the task the old folder keeps its blocks: remember it for the uninstall
  if WizardIsTaskSelected('mpv') then
    SetPreviousData(PreviousDataKey, 'MpvDir', MpvDirPage.Values[0])
  else if OldMpvDir <> '' then
    SetPreviousData(PreviousDataKey, 'MpvDir', OldMpvDir);
end;

procedure RunMpvConf(Action, Dir: String);
var
  Code: Integer;
begin
  Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\mpvconf.ps1') + '" -Action ' +
    Action + ' -Path "' + AddBackslash(Dir) + 'mpv.conf"', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

// Configure the chosen folder; reinstalled with another one: take the blocks out of the old one
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('mpv') then begin
    if (OldMpvDir <> '') and (CompareText(RemoveBackslash(OldMpvDir), RemoveBackslash(MpvDirPage.Values[0])) <> 0) then
      RunMpvConf('remove', OldMpvDir);
    RunMpvConf('add', MpvDirPage.Values[0]);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
begin
  // usUninstall runs before {app} is deleted (mpvconf.ps1 lives there). Installs without the value used %APPDATA%\mpv.
  if CurUninstallStep = usUninstall then begin
    if not RegQueryStringValue(HKCU, UninstallKey, 'Inno Setup CodeFile: MpvDir', Dir) then
      Dir := ExpandConstant('{#MpvDefaultDir}');
    RunMpvConf('remove', Dir);
  end;
end;
