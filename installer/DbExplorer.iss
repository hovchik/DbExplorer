; DB Explorer installer for Windows (Inno Setup 6.3+ or 7).
;
; Build it with build-installer.ps1 next to this file; it publishes the app and passes AppVersion
; and PublishDir. By hand:  ISCC.exe /DAppVersion=1.2.0 /DPublishDir=out\publish DbExplorer.iss
;
; What it does on the user's PC:
;   - installs a self-contained build (no .NET to install), for the current user without an administrator
;     prompt, or for everyone in Program Files if they pick that (or pass /ALLUSERS)
;   - Start menu shortcut, optional desktop shortcut
;   - DB Explorer in Explorer's "Open with" list for .sql files, and optionally as their default app
;   - opens the app at the end
; The user's connections, settings, metadata cache and query tabs (%LOCALAPPDATA%\DbExplorer) are never
; touched, so upgrades and uninstalls keep them.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef NumericVersion
  ; Windows version resources take numbers only (1.2.0 for 1.2.0-rc1).
  #define NumericVersion AppVersion
#endif
#ifndef PublishDir
  #define PublishDir "out\publish"
#endif
#define AppName "DB Explorer"
#define AppExe "DbExplorer.exe"

[Setup]
; Keep this id forever: it is how a new version finds and upgrades the installed one.
AppId={{305694F8-7811-4858-9596-EF6CE0E669B4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
AppCopyright=Copyright (c) 2026 Hovhannes
AppPublisherURL=https://github.com/hovchik/DbExplorer
AppSupportURL=https://github.com/hovchik/DbExplorer/issues
AppUpdatesURL=https://github.com/hovchik/DbExplorer/releases
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
; A developer tool: install for the current user by default (no UAC prompt, %LOCALAPPDATA%\Programs),
; or for all users in Program Files from the first page or with /ALLUSERS.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; An open DB Explorer is closed for an upgrade.
CloseApplications=force
RestartApplications=no
SetupIconFile=..\src\DbExplorer.Desktop\Assets\logo.ico
WizardImageFile=art\wizard.bmp
WizardSmallImageFile=art\wizard-small.bmp
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Tells Explorer to pick up the .sql registration below without a sign-out.
ChangesAssociations=yes
OutputDir=out
OutputBaseFilename=DbExplorer-Setup-{#AppVersion}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; Off by default: SSMS or another editor may already own .sql files. Windows 10/11 can still keep the app the user
; picked in "Open with > Always", and then this only adds DB Explorer to that list.
Name: "sqldefault"; Description: "Open .sql files with DB Explorer by default"; GroupDescription: "File types:"; Flags: unchecked

[Registry]
; HKA: the current user's classes for a per-user install, the machine's for an all-users one.
; A ProgID of our own; uninstall removes it whole.
Root: HKA; Subkey: "Software\Classes\DbExplorer.sql"; ValueType: string; ValueName: ""; ValueData: "SQL script"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\DbExplorer.sql\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\DbExplorer.sql\shell\open"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"
Root: HKA; Subkey: "Software\Classes\DbExplorer.sql\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
; Always: DB Explorer appears in "Open with" for .sql files, without taking them over.
Root: HKA; Subkey: "Software\Classes\.sql\OpenWithProgids"; ValueType: string; ValueName: "DbExplorer.sql"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".sql"; ValueData: ""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
; Only with the task: .sql files open in DB Explorer on a double-click.
Root: HKA; Subkey: "Software\Classes\.sql"; ValueType: string; ValueName: ""; ValueData: "DbExplorer.sql"; Tasks: sqldefault; Flags: uninsdeletevalue

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; DestName: "THIRD-PARTY-NOTICES.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Open the app as the person who ran setup (not an elevated admin), so its data lands in their own profile.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
