; ---------------------------------------------------------------------------
;  Inno Setup script for PrintPacer.
;
;  Build with:
;      winget install JRSoftware.InnoSetup
;      .\build.ps1 -Installer
;
;  Produces installer\output\PrintPacer-Setup-1.4.0.exe: a single, self-contained
;  executable an ordinary user can download and double-click.
;
;  Installs for all users into Program Files by default, which is what a
;  Windows user expects of a desktop application, but the folder page is always
;  shown so the user can put it anywhere else - another drive, say. PrintPacer
;  itself never writes to its install directory: jobs, settings and logs all
;  live in the per-user %LOCALAPPDATA%\PrintPacer, so it runs perfectly well as
;  a standard user afterwards. Only the installation itself needs elevation.
;
;  Because the folder is the user's choice, setup checks it before installing
;  (see [Code]): it must be a folder of PrintPacer's own, never a drive root, a
;  folder already full of other files, or the data folder.
;
;  A user without administrator rights can still install: Inno offers a per-user
;  install instead, via PrivilegesRequiredOverridesAllowed.
; ---------------------------------------------------------------------------

#define AppName        "PrintPacer"
#define AppVersion     "1.4.0"
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

; Always offer the folder page. The default, auto, hides it whenever an earlier
; version is installed, which left an upgrading user no way to choose. The
; previous folder is still pre-filled, so a plain upgrade is just Next.
DisableDirPage=no
UsePreviousAppDir=yes
AlwaysShowDirOnReadyPage=yes
; [Code] gives a more useful warning about an existing folder than Inno's own.
DirExistsWarning=no

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
; Only the folder itself, once the files setup installed have gone. Never
; filesandordirs: the folder is the user's choice, and anything else they keep
; in it is not ours to delete.
Type: dirifempty; Name: "{app}"

[Messages]
; The data directory is deliberately left in place: it holds the job database
; and the logs, which someone may still want after uninstalling.
ConfirmUninstall=Remove %1?%n%nYour jobs, settings and logs in%n%n    {localappdata}\{#AppName}%n%nwill be kept. Delete that folder by hand if you want them gone as well.

[Code]
const
  ExeName = '{#AppExe}';

var
  DataFolderLabel: TNewStaticText;

function DataFolder: String;
begin
  Result := ExpandConstant('{localappdata}\{#AppName}');
end;

function NormalisedPath(const Path: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Trim(Path)));
end;

function SamePath(const A, B: String): Boolean;
begin
  Result := CompareText(NormalisedPath(A), NormalisedPath(B)) = 0;
end;

{ True when Child is Parent itself or anywhere beneath it. }
function IsWithin(const Child, Parent: String): Boolean;
begin
  Result := Pos(Uppercase(AddBackslash(NormalisedPath(Parent))),
                Uppercase(AddBackslash(NormalisedPath(Child)))) = 1;
end;

function IsDriveRoot(const Path: String): Boolean;
begin
  Result := SamePath(Path, AddBackslash(ExtractFileDrive(NormalisedPath(Path))));
end;

function IsEmptyFolder(const Path: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Path) + '*', FindRec) then
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        Result := False;
    until (not Result) or (not FindNext(FindRec));
  finally
    FindClose(FindRec);
  end;
end;

{ Can setup create files there? Tested on the folder itself, or on the nearest
  folder above it that exists, since setup creates the rest. Catches a per-user
  install pointed at Program Files, a read-only or disconnected drive, and a
  folder the user has no rights to. }
function CanWriteTo(const Path: String): Boolean;
var
  Existing, Probe: String;
begin
  Existing := NormalisedPath(Path);
  while (not DirExists(Existing)) and (Existing <> ExtractFileDir(Existing)) do
    Existing := ExtractFileDir(Existing);

  Probe := AddBackslash(Existing) + '.printpacer-setup-write-test';
  Result := SaveStringToFile(Probe, '', False);
  if Result then
    DeleteFile(Probe);
end;

{ Checks the chosen folder before anything is installed. A folder that would not
  be PrintPacer's own is offered a PrintPacer subfolder instead; a folder that
  cannot work at all is refused, with the reason. }
function CheckInstallFolder: Boolean;
var
  Dir, Suggested: String;
begin
  Result := False;
  Dir := NormalisedPath(WizardDirValue);

  while True do
  begin
    if IsWithin(Dir, ExpandConstant('{win}')) then
    begin
      SuppressibleMsgBox('PrintPacer cannot be installed inside the Windows folder.' + #13#10#13#10 +
        'Please choose another folder.', mbError, MB_OK, IDOK);
      Exit;
    end;

    if IsWithin(Dir, DataFolder) then
    begin
      SuppressibleMsgBox('That is where PrintPacer keeps its jobs, settings and logs:' + #13#10#13#10 +
        '    ' + DataFolder + #13#10#13#10 +
        'The program has to go somewhere else, so that updating or removing it can never touch your jobs. ' +
        'Please choose another folder.', mbError, MB_OK, IDOK);
      Exit;
    end;

    { Removing PrintPacer removes its folder, so the folder must be PrintPacer's
      own: not a drive root, and not one already full of something else. }
    if IsDriveRoot(Dir) or
       (DirExists(Dir) and (not IsEmptyFolder(Dir)) and (not FileExists(AddBackslash(Dir) + ExeName))) then
    begin
      Suggested := AddBackslash(Dir) + '{#AppName}';
      if SuppressibleMsgBox(Dir + #13#10#13#10 +
           'is not empty. PrintPacer should have a folder of its own, so that removing it ' +
           'later removes only PrintPacer.' + #13#10#13#10 +
           'Install into this folder instead?' + #13#10#13#10 +
           '    ' + Suggested,
           mbConfirmation, MB_YESNO, IDYES) <> IDYES then
        Exit;

      Dir := Suggested;
      WizardForm.DirEdit.Text := Dir;
    end
    else
      Break;
  end;

  if not CanWriteTo(Dir) then
  begin
    if IsAdminInstallMode then
      SuppressibleMsgBox('Setup cannot write to:' + #13#10#13#10 + '    ' + Dir + #13#10#13#10 +
        'The drive may be read-only, full or disconnected. Please choose another folder.',
        mbError, MB_OK, IDOK)
    else
      SuppressibleMsgBox('You do not have permission to write to:' + #13#10#13#10 + '    ' + Dir + #13#10#13#10 +
        'Choose a folder in your own profile, such as' + #13#10#13#10 +
        '    ' + ExpandConstant('{userpf}\{#AppName}') + #13#10#13#10 +
        'or run setup again and choose to install for all users.',
        mbError, MB_OK, IDOK);
    Exit;
  end;

  Result := True;
end;

procedure InitializeWizard;
begin
  { Say on the folder page that the data lives elsewhere, so nobody picks a
    folder thinking they are choosing where their jobs go. }
  DataFolderLabel := TNewStaticText.Create(WizardForm);
  DataFolderLabel.Parent := WizardForm.SelectDirPage;
  DataFolderLabel.Left := WizardForm.DirEdit.Left;
  DataFolderLabel.Top := WizardForm.DirEdit.Top + WizardForm.DirEdit.Height + ScaleY(20);
  DataFolderLabel.Width := WizardForm.DirBrowseButton.Left + WizardForm.DirBrowseButton.Width - DataFolderLabel.Left;
  DataFolderLabel.AutoSize := False;
  DataFolderLabel.WordWrap := True;
  DataFolderLabel.Height := ScaleY(48);
  DataFolderLabel.Caption :=
    'This folder holds the program only. Your jobs, settings and logs are kept separately in ' +
    DataFolder + ', and are left alone when PrintPacer is updated, moved or removed.';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
    Result := CheckInstallFolder;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo;

  if (WizardForm.PrevAppDir <> '') and (not SamePath(WizardForm.PrevAppDir, WizardDirValue)) then
    Result := Result + NewLine + NewLine +
              'Moving from (the old copy is removed):' + NewLine + Space + WizardForm.PrevAppDir;

  Result := Result + NewLine + NewLine +
            'Jobs, settings and logs (kept as they are):' + NewLine + Space + DataFolder;

  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

{ Deletes from OldRoot each file that setup has just installed into NewRoot,
  matched by name, so only PrintPacer's own files leave the old folder. }
procedure DeleteInstalledFiles(const NewRoot, OldRoot, Rel: String);
var
  FindRec: TFindRec;
begin
  if FindFirst(NewRoot + Rel + '*', FindRec) then
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          DeleteInstalledFiles(NewRoot, OldRoot, Rel + FindRec.Name + '\');
          RemoveDir(OldRoot + Rel + FindRec.Name);
        end
        else
          DeleteFile(OldRoot + Rel + FindRec.Name);
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

{ An upgrade into a different folder: once the new copy is in place, remove the
  old one, so there are not two copies and the stale one cannot be started by
  mistake. Shortcuts, the startup entry and Open with have already been
  rewritten to the new folder by then. }
procedure RemovePreviousCopy;
var
  OldDir, NewDir: String;
begin
  OldDir := WizardForm.PrevAppDir;
  NewDir := ExpandConstant('{app}');

  if (OldDir = '') or IsWithin(OldDir, NewDir) or IsWithin(NewDir, OldDir) then
    Exit;
  if not FileExists(AddBackslash(OldDir) + ExeName) then
    Exit;

  Log('Moved from ' + OldDir + ' to ' + NewDir + '; removing the old copy');
  DeleteInstalledFiles(AddBackslash(NewDir), AddBackslash(OldDir), '');
  DeleteFile(AddBackslash(OldDir) + 'unins000.exe');
  DeleteFile(AddBackslash(OldDir) + 'unins000.dat');
  DeleteFile(AddBackslash(OldDir) + 'unins000.msg');

  if not RemoveDir(OldDir) then
    Log('Left ' + OldDir + ' in place: it still holds files setup did not install');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RemovePreviousCopy;
end;
