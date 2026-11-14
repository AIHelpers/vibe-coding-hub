; Inno Setup script for the Windows installer: dist\VibeCodingHub-<version>-win-x64-setup.exe
;
; Build (from the repository root, after publishing the app and the CLI to publish\app and publish\cli):
;   ISCC.exe /DAppVersion=1.2.0 /DAppVersionNumeric=1.2.0 packaging\windows\setup.iss
; Silent install: VibeCodingHub-<version>-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
; Without the aiagent command on PATH: add /TASKS="!addcli"

#define AppName "Vibe Coding Hub"
#define AppExe  "Vibe Coding Hub.exe"
#define AppPublisher "AIHelpers"
#define AppUrl "https://github.com/AIHelpers/vibe-coding-hub"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; Windows file versions are numeric (a.b.c.d): the release version without any pre-release suffix.
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.0.0"
#endif

[Setup]
; Identifies this product for upgrades and uninstall. Never change it.
AppId={{CF84457E-26BC-4F88-9CA2-412A5A74728C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersionNumeric}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\..\LICENSE
; Per-user install by default (no administrator rights); the wizard offers an all-users install.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; The oldest Windows that .NET 10 supports.
MinVersion=10.0.14393
OutputDir=..\..\dist
OutputBaseFilename=VibeCodingHub-{#AppVersion}-win-x64-setup
SetupIconFile=..\icons\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "addcli"; Description: "Install the &aiagent command-line tool and add it to PATH"

[Files]
Source: "..\..\publish\app\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion
Source: "..\..\publish\cli\*"; DestDir: "{app}\cli"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion; Tasks: addcli

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
// The aiagent command lives in {app}\cli. It is added to the PATH of the user (per-user install) or of the machine
// (all-users install), and removed again on uninstall.

function EnvRoot(): Integer;
begin
  if IsAdminInstallMode then
    Result := HKLM
  else
    Result := HKCU;
end;

function EnvKey(): String;
begin
  if IsAdminInstallMode then
    Result := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
  else
    Result := 'Environment';
end;

procedure AddToPath(const Dir: String);
var
  Paths: String;
begin
  if not RegQueryStringValue(EnvRoot(), EnvKey(), 'Path', Paths) then
    Paths := '';
  // Already there (compared whole entry, ignoring case): nothing to do.
  if Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0 then
    Exit;
  if (Paths <> '') and (Copy(Paths, Length(Paths), 1) <> ';') then
    Paths := Paths + ';';
  if not RegWriteExpandStringValue(EnvRoot(), EnvKey(), 'Path', Paths + Dir) then
    Log('Could not add ' + Dir + ' to PATH.');
end;

procedure RemoveFromPath(const Dir: String);
var
  Paths, Padded: String;
  P: Integer;
begin
  if not RegQueryStringValue(EnvRoot(), EnvKey(), 'Path', Paths) then
    Exit;
  // Work on ";a;b;c;" so that every entry, the first and the last included, is surrounded by separators.
  Padded := ';' + Paths + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(Padded));
  if P = 0 then
    Exit;
  Delete(Padded, P, Length(Dir) + 1);
  if Length(Padded) <= 2 then
    Paths := ''
  else
    Paths := Copy(Padded, 2, Length(Padded) - 2);
  if not RegWriteExpandStringValue(EnvRoot(), EnvKey(), 'Path', Paths) then
    Log('Could not remove ' + Dir + ' from PATH.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addcli') then
    AddToPath(ExpandConstant('{app}\cli'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath(ExpandConstant('{app}\cli'));
end;
