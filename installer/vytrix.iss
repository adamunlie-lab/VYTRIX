; VYTRIX installer (Inno Setup 6). Build with: .\build.ps1 -Installer [-Version x.y.z]
#define AppName "VYTRIX"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define Src "..\dist\VYTRIX"

[Setup]
AppId={{6E2B7C1A-5D4F-4C2B-9A7E-3F1D5B8C0A21}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=VYTRIX
AppVerName={#AppName} {#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=VYTRIX-Setup-{#AppVersion}
SetupIconFile=..\src\Vytrix\Assets\vytrix.ico
UninstallDisplayIcon={app}\VYTRIX.exe
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
PrivilegesRequired=admin
WizardStyle=modern
; Shows the VYTRIX Terms of Agreement; setup cannot continue unless the user accepts.
LicenseFile=..\legal\TERMS.txt
; Updates install over the existing copy (same AppId); running VYTRIX is closed first.
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#Src}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\legal\TERMS.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\VYTRIX.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\VYTRIX.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VYTRIX.exe"; Description: "Launch VYTRIX"; Flags: nowait postinstall skipifsilent
; After an in-app update (/VYTRIXUPDATE=1), reopen VYTRIX as the signed-in user, not elevated.
Filename: "{app}\VYTRIX.exe"; Flags: nowait runasoriginaluser; Check: IsInAppUpdate

[Code]
function IsInAppUpdate: Boolean;
begin
  Result := ExpandConstant('{param:VYTRIXUPDATE|0}') = '1';
end;

// User data (%LocalAppData%\VYTRIX: change journal, backups, logs) is intentionally
// kept on uninstall so changes made by VYTRIX can still be reviewed and reverted.
