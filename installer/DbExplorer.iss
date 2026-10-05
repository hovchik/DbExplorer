; DbExplorer installer (Inno Setup 6.3+ or 7).
;
; Build it with build-installer.ps1 next to this file; it publishes the app and passes AppVersion
; and PublishDir. By hand:  ISCC.exe /DAppVersion=1.2.0 /DPublishDir=out\publish DbExplorer.iss
;
; What it does:
;   - installs a self-contained, ReadyToRun build (no .NET to install) into Program Files
;   - Start menu shortcut, optional desktop shortcut
;   - opens the app at the end
; Each user's connections, metadata cache and query tabs (%LOCALAPPDATA%\DbExplorer) are never touched,
; so upgrades and uninstalls keep them.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "out\publish"
#endif
#define AppName "DbExplorer"
#define AppExe "DbExplorer.exe"

[Setup]
; Keep this id forever: it is how a new version finds and upgrades the installed one.
AppId={{FB5CEC9C-25FE-4E55-9CBC-DB41E689F93A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
; Program Files by default; the user may choose "only for me" to install without an administrator.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; An open DbExplorer is closed for an upgrade.
CloseApplications=force
RestartApplications=no
SetupIconFile=..\src\DbExplorer.Desktop\Assets\logo.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir=out
OutputBaseFilename=DbExplorer-Setup-{#AppVersion}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
