; ---------------------------------------------------------------------------
;  Inno Setup script for PrintPacer.
;
;  Build with:
;      winget install JRSoftware.InnoSetup
;      .\build.ps1 -Installer
;
;  Produces installer\output\PrintPacer-Setup-1.3.0.exe: a single, self-contained
;  executable an ordinary user can download and double-click.
;
;  Installs for all users into Program Files, which is what a Windows user
;  expects of a desktop application. PrintPacer itself never writes to its
;  install directory: jobs, settings and logs all live in the per-user
;  %LOCALAPPDATA%\PrintPacer, so it runs perfectly well as a standard user
;  afterwards. Only the installation itself needs elevation.
;
;  A user without administrator rights can still install: Inno offers a per-user
;  install instead, via PrivilegesRequiredOverridesAllowed.
; ---------------------------------------------------------------------------

#define AppName        "PrintPacer"
#define AppVersion     "1.3.0"
#define AppPublisher   "PrintPacer"
#define AppExe         "PrintPacer.exe"
#define AppDescription "Scheduled PDF batch printing"
#define SourceDir      "..\publish\PrintPacer"

[Setup]
AppId={{8F3C2A41-9D57-4E2B-B6A1-7C5E0D4F91A2}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppComments={#AppDescription}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; Install for all users into Program Files. A user who cannot elevate is offered
; a per-user install instead rather than being turned away.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\PrintPacer.App\Assets\printpacer.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no

; 64-bit only: PDFium and SQLite are loaded in-process as native x64 libraries.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Detect a running copy so setup can ask the user to close it rather than
; failing on a locked file. The name matches the single-instance mutex the
; application itself creates.
AppMutex=Global\PrintPacer.SingleInstance.v1
SetupMutex={#AppName}SetupMutex
CloseApplications=yes
RestartApplications=no

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
; The published folder is flat, but recursesubdirs/createallsubdirs keeps the
; structure intact should the layout ever gain subdirectories.
Source: "{#SourceDir}\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; \
    Comment: "Print large PDFs in paced batches, resting the printer in between"

Name: "{group}\{#AppName} data folder"; Filename: "{localappdata}\{#AppName}"; \
    Comment: "Jobs, settings and logs"

Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; \
    Comment: "Print large PDFs in paced batches"; Tasks: desktopicon

[Registry]
; Startup entry, written to the hive of the user running setup.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"""; \
    Flags: uninsdeletevalue; Tasks: startup and not startupmin

Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"" --minimised"; \
    Flags: uninsdeletevalue; Tasks: startup and startupmin

; "Open with PrintPacer" on any PDF. Registered machine-wide as an application
; and added to the PDF open-with list, but deliberately NOT as the default
; handler: the user's usual PDF reader is left exactly as it was.
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; \
    ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; \
    Flags: uninsdeletekey

Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; \
    ValueType: string; ValueName: ".pdf"; ValueData: ""; \
    Flags: uninsdeletekey

Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; \
    ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; \
    Flags: uninsdeletekey

Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithList\{#AppExe}"; \
    Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExe}"; \
    Description: "Start {#AppName} now"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; Close a running instance before removing its files, so the uninstall does not
; leave a locked folder behind.
Filename: "{cmd}"; Parameters: "/C taskkill /IM ""{#AppExe}"" /F"; \
    Flags: runhidden; RunOnceId: "StopPrintPacer"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Messages]
; The data directory is deliberately left in place: it holds the job database
; and the logs, which someone may still want after uninstalling.
ConfirmUninstall=Remove %1?%n%nYour jobs, settings and logs in%n%n    {localappdata}\{#AppName}%n%nwill be kept. Delete that folder by hand if you want them gone as well.
