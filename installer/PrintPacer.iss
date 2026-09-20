; ---------------------------------------------------------------------------
;  Inno Setup script for PrintPacer.
;
;  Build with:
;      winget install JRSoftware.InnoSetup
;      .\build.ps1 -Installer
;
;  Produces installer\output\PrintPacer-Setup-1.0.0.exe.
;
;  Deliberately a per-user install (PrivilegesRequired=lowest). PrintPacer needs
;  no administrator rights: reading print queues, submitting jobs and cancelling
;  its own jobs are all available to a standard user, and its data lives under
;  the user's own profile. Asking for elevation would be both unnecessary and a
;  nuisance for something meant to sit in the notification area overnight.
; ---------------------------------------------------------------------------

#define AppName        "PrintPacer"
#define AppVersion     "1.0.0"
#define AppPublisher   "PrintPacer"
#define AppExe         "PrintPacer.exe"
#define SourceDir      "..\publish\PrintPacer"

[Setup]
AppId={{8F3C2A41-9D57-4E2B-B6A1-7C5E0D4F91A2}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\PrintPacer.App\Assets\printpacer.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; The application is self-contained, so there is no .NET prerequisite to check.
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; \
    Description: "Create a &Desktop shortcut"; \
    GroupDescription: "Shortcuts:"; \
    Flags: unchecked

Name: "startup"; \
    Description: "Start {#AppName} when I sign in to Windows"; \
    GroupDescription: "Startup:"; \
    Flags: unchecked

Name: "startupmin"; \
    Description: "Start minimised to the notification area"; \
    GroupDescription: "Startup:"; \
    Flags: unchecked; \
    Check: WizardIsTaskSelected('startup')

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; \
    Comment: "Print large PDFs in paced batches, resting the printer in between"

Name: "{group}\{#AppName} data folder"; Filename: "{localappdata}\{#AppName}"; \
    Comment: "Jobs, settings and logs"

Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Per-user startup entry. Written under HKCU so it works without elevation and
; the user can see and disable it from Task Manager's Startup tab.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"""; \
    Flags: uninsdeletevalue; Tasks: startup and not startupmin

Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"" --minimised"; \
    Flags: uninsdeletevalue; Tasks: startup and startupmin

; "Open with PrintPacer" on any PDF. Registered as a verb rather than as the
; default handler, so the user's usual PDF reader is left alone.
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}"; \
    ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; \
    Flags: uninsdeletekey

Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; \
    ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; \
    Flags: uninsdeletekey

Root: HKCU; Subkey: "Software\Classes\.pdf\OpenWithList\{#AppExe}"; \
    Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExe}"; \
    Description: "Start {#AppName} now"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; Close a running instance before removing its files, so the uninstall does not
; leave a locked folder behind.
Filename: "{cmd}"; Parameters: "/C taskkill /IM ""{#AppExe}"" /F"; \
    Flags: runhidden skipifdoesntexist

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Messages]
; The data directory is deliberately not removed: it holds the job database and
; the logs, which someone may still need after uninstalling.
ConfirmUninstall=Remove %1?%n%nYour jobs, settings and logs in %n%n    {localappdata}\{#AppName}%n%nwill be kept. Delete that folder by hand if you want them gone as well.
