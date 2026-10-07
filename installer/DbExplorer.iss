; DB Explorer installer for Windows (Inno Setup 6.3+ or 7).
;
; Build it with build-installer.ps1 next to this file; it publishes the app and passes AppVersion
; and PublishDir. By hand:  ISCC.exe /DAppVersion=1.2.0 /DPublishDir=out\publish DbExplorer.iss
;
; What it does on the user's PC:
;   - installs a self-contained build (no .NET to install), for the current user without an administrator
;     prompt, or for everyone in Program Files if they pick that (or pass /ALLUSERS)
;   - Start menu shortcut, optional desktop shortcut
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
OutputDir=out
OutputBaseFilename=DbExplorer-Setup-{#AppVersion}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Open the app as the person who ran setup (not an elevated admin), so its data lands in their own profile.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
