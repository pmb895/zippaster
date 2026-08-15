; Inno Setup script for ZipPaster.
; Build with installer/build.ps1, which publishes the app first and then invokes
; ISCC.exe with the version number passed in.

#define AppName "ZipPaster"
#define AppPublisher "ZipPaster"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\src\ZipPaster\bin\Release\net10.0-windows\win-x64\publish"
#endif

[Setup]
AppId={{8F3C2A41-6B9D-4E77-9A5C-1D2E4F6A8B31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=LICENSE.txt
OutputDir=output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Installs per-user so no administrator prompt appears. The app must never run
; elevated: Windows UIPI would block it from typing into a normal browser.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\ZipPaster.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "startupicon"; Description: "Start {#AppName} automatically when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\ZipPaster.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\ZipPaster.exe"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\ZipPaster.exe"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\ZipPaster.exe"; Tasks: startupicon

[Run]
Filename: "{app}\ZipPaster.exe"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Deliberately does NOT remove %LOCALAPPDATA%\ZipPaster: that folder holds the
; user's projects and used-ZIP history, which must survive an upgrade. It is
; left in place on uninstall too, so a reinstall picks up where they left off.
Type: dirifempty; Name: "{app}"

[Code]
// The app registers its own Run entry when the in-app "start at sign in"
// setting is used. Remove it on uninstall so no orphan entry is left behind.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'ZipPaster');
  end;
end;
