; ============================================================================
;  Camera Kill Switch - Inno Setup installer
;  Produces a single self-contained CameraKillSwitch-Setup.exe:
;    - UAC-elevated wizard
;    - installs the self-contained tray app to Program Files
;    - optional all-users autostart at sign-in
;    - proper Add/Remove Programs entry with a working uninstaller
;
;  The app is self-provisioning: on first toggle it writes its engine to
;  %ProgramData%\CameraKillSwitch and registers the elevated scheduled tasks
;  itself (one UAC prompt). The installer therefore does NOT register those
;  tasks -- doing so would reintroduce the standard-user "Access is denied"
;  bug documented in PROGRESS.md. Teardown of those runtime artifacts is
;  handled by uninstall-cleanup.ps1 during uninstall.
;
;  Build:  powershell -ExecutionPolicy Bypass -File Build-Installer.ps1
; ============================================================================

#define AppName        "Camera Kill Switch"
#define AppVersion     "1.0.0"
#define AppPublisher   "Fayyaz"
#define AppExeName     "CameraKillSwitch.exe"
#define AppUrl         "https://github.com/"

[Setup]
; Stable AppId -- keep this GUID constant across versions so upgrades and the
; Add/Remove Programs entry are recognized as the same product.
AppId={{9C4B2E7A-3F1D-4A5C-9B8E-2D6F1A7C4E30}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\app\app.ico
OutputDir=..\..\dist
OutputBaseFilename=CameraKillSwitch-Setup
Compression=lzma2
SolidCompression=yes
; Whole-machine install: needs admin. This also makes the uninstaller run
; elevated, which the cleanup step relies on (unregistering tasks, HKLM).
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start {#AppName} automatically at sign-in (recommended)"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\app\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall-cleanup.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; All-users autostart. Written only if the user keeps the "autostart" task.
; uninsdeletevalue removes it on uninstall.
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "CameraKillSwitchTray"; \
    ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName} now"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; Runs BEFORE files are removed. Re-enables the camera (so no one is left with
; it stuck off), stops the running tray app, and removes the scheduled tasks +
; %ProgramData%\CameraKillSwitch engine folder the app created at runtime.
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\uninstall-cleanup.ps1"""; \
    Flags: runhidden waituntilterminated; \
    RunOnceId: "CameraKillSwitchCleanup"
