; Inno Setup script for the Windows installer: PPObjectSearch.exe and ppos.exe, a Start menu
; shortcut, ppos on PATH, and an uninstaller. The release workflow compiles it after publishing;
; to build it by hand, publish both exes to publish\ (see README) and run, from the repository root:
;
;   & "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=1.2.3 packaging\installer\PPObjectSearch.iss
;
; It installs for the current user by default (%LOCALAPPDATA%\Programs, no admin rights needed);
; the first page offers an install for all users instead. Settings and logs in
; %LOCALAPPDATA%\PPObjectSearch are the app's own and are left alone by the uninstaller.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
; The file version must be numbers only, so a pre-release tag (1.2.3-beta) keeps just 1.2.3.
#ifndef FileVersion
  #define FileVersion AppVersion
#endif
; Where the published exes are, and where the installer is written.
#ifndef PublishDir
  #define PublishDir AddBackslash(SourcePath) + "..\..\publish"
#endif
#ifndef OutputDir
  #define OutputDir PublishDir
#endif

[Setup]
; Never change AppId: it is how an upgrade finds the version already installed.
AppId={{4AB7B333-A652-4E9B-B199-5E4059677143}
AppName=Power Platform Object Search
AppVersion={#AppVersion}
AppVerName=Power Platform Object Search {#AppVersion}
AppPublisher=Tom Norton
AppPublisherURL=https://github.com/tmnrtn/PPObjectSearch
AppSupportURL=https://github.com/tmnrtn/PPObjectSearch/issues
AppUpdatesURL=https://github.com/tmnrtn/PPObjectSearch/releases
AppCopyright=Copyright (c) 2026 Tom Norton
VersionInfoVersion={#FileVersion}
VersionInfoProductVersion={#FileVersion}
VersionInfoDescription=Power Platform Object Search Setup

; Per-user by default; the dialog (or /ALLUSERS on the command line) installs for everyone.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={autopf}\PPObjectSearch
DisableProgramGroupPage=yes
DisableDirPage=auto
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Upgrading over a running copy asks to close it rather than leaving the old exe in place.
CloseApplications=yes
RestartApplications=no
ChangesEnvironment=yes

SetupIconFile=..\..\appicon.ico
UninstallDisplayIcon={app}\PPObjectSearch.exe
UninstallDisplayName=Power Platform Object Search
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=PPObjectSearch-Setup

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: addtopath; Description: "Add the ppos command line to PATH"; GroupDescription: "Command line:"

[Files]
Source: "{#PublishDir}\PPObjectSearch.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\ppos.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Power Platform Object Search"; Filename: "{app}\PPObjectSearch.exe"
Name: "{autodesktop}\Power Platform Object Search"; Filename: "{app}\PPObjectSearch.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PPObjectSearch.exe"; Description: "{cm:LaunchProgram,Power Platform Object Search}"; Flags: nowait postinstall skipifsilent

[Code]
// PATH is the user's for a per-user install and the machine's for an all-users one.
function EnvRoot: Integer;
begin
  if IsAdminInstallMode then
    Result := HKEY_LOCAL_MACHINE
  else
    Result := HKEY_CURRENT_USER;
end;

function EnvKey: String;
begin
  if IsAdminInstallMode then
    Result := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
  else
    Result := 'Environment';
end;

procedure AddToPath(Dir: String);
var
  Paths: String;
begin
  if not RegQueryStringValue(EnvRoot, EnvKey, 'Path', Paths) then
    Paths := '';
  if Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0 then
    exit;
  if (Paths <> '') and (Paths[Length(Paths)] <> ';') then
    Paths := Paths + ';';
  RegWriteExpandStringValue(EnvRoot, EnvKey, 'Path', Paths + Dir);
end;

procedure RemoveFromPath(Dir: String);
var
  Paths: String;
  P: Integer;
begin
  if not RegQueryStringValue(EnvRoot, EnvKey, 'Path', Paths) then
    exit;
  // Wrapped in separators so the match is a whole entry, then unwrapped again.
  Paths := ';' + Paths + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(Paths));
  if P = 0 then
    exit;
  Delete(Paths, P, Length(Dir) + 1);
  RegWriteExpandStringValue(EnvRoot, EnvKey, 'Path', Copy(Paths, 2, Length(Paths) - 2));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddToPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;
