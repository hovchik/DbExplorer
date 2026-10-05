# Installer

`DbExplorer-Setup-<version>.exe` installs DB Explorer on Windows 10/11 (64-bit). It needs nothing else installed
first: .NET comes inside.

## What the user sees

1. Install for *me only* (default, no administrator prompt, into `%LOCALAPPDATA%\Programs\DB Explorer`) or for
   *all users* (Program Files, asks for an administrator), then where to install.
2. *Create a desktop shortcut* (unticked by default). The Start menu always gets *DB Explorer*.
3. **Launch DB Explorer** at the end.

Upgrading: run the newer installer. It closes the app and never touches the user's data in
`%LOCALAPPDATA%\DbExplorer` (connections, metadata cache, query tabs, API collections, `logs\errors.log`).
Uninstalling removes the program and its shortcuts and also leaves the data.

For IT, unattended: `DbExplorer-Setup-1.2.0.exe /VERYSILENT /ALLUSERS /TASKS="desktopicon"`
(`/CURRENTUSER` instead of `/ALLUSERS` for a per-user install).

## Building it

On Windows, with the .NET 8 SDK. The script also needs Inno Setup 6.3+ or 7; if it can't find it, it installs it
with winget the first time (or get it from https://jrsoftware.org/isdl.php, or pass `-Iscc <path to ISCC.exe>`):

```powershell
.\installer\build-installer.ps1 -Version 1.2.0
```

It publishes with the `FolderProfile` publish profile (self-contained, ReadyToRun, win-x64) into
`installer\out\publish` and compiles `DbExplorer.iss`. The result is `installer\out\DbExplorer-Setup-1.2.0.exe`.
GitHub Actions builds the same file on every pull request that touches the app or the installer, and on tags like
`v1.2.0` (*Windows installer* workflow → artifacts).

Files: `DbExplorer.iss` (the installer), `build-installer.ps1`, `art\` (wizard pictures, made from
`src/DbExplorer.Desktop/Assets/logo.png`).

The installer isn't code-signed yet, so Windows SmartScreen shows *"Windows protected your PC"* the first
time; *More info → Run anyway*. Signing it needs a code-signing certificate (`SignTool=` in `DbExplorer.iss`).
