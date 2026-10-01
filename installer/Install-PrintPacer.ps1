<#
.SYNOPSIS
    Installs or removes PrintPacer for the current user.

.DESCRIPTION
    A per-user install: everything goes under %LOCALAPPDATA%, so no administrator
    rights are needed and nothing outside your own profile is touched. PrintPacer
    itself needs no elevated privileges to read print queues, submit jobs or
    cancel its own jobs, so the installer should not demand them either.

    Creates:
      - the application in a folder you choose; %LOCALAPPDATA%\Programs\PrintPacer
        unless you pick another, or wherever the previous version went
      - a Start Menu shortcut
      - a Desktop shortcut, if asked for
      - an entry in Settings > Apps > Installed apps, so it uninstalls the
        ordinary way

    Leaves alone (so an upgrade never loses a running job):
      - the data folder, which holds the job database, settings and logs:
        %LOCALAPPDATA%\PrintPacer unless you choose another

    Run interactively, it asks where to install, and then where to keep the
    jobs, settings and logs, before copying anything: press Enter for the
    suggested folder, B to browse for one, or type a path.

    The script never moves data. A different data folder is recorded in
    HKCU\Software\PrintPacer as PendingDataFolder, and PrintPacer moves the data
    when it next starts, copying and checking every file before removing the
    old ones.

.PARAMETER InstallDir
    Install into this folder without asking. It must be a folder of PrintPacer's
    own: a folder that already holds other files gets a PrintPacer subfolder.

.PARAMETER DataDir
    Keep the jobs, settings and logs in this folder, without asking. Existing
    data moves there when PrintPacer next starts.

.PARAMETER Uninstall
    Remove the application, its shortcuts and its Installed apps entry.

.PARAMETER RemoveData
    With -Uninstall, also delete the job database, settings and logs, wherever
    they are kept.

.PARAMETER DesktopShortcut
    Also place a shortcut on the Desktop.

.PARAMETER Quiet
    Suppress progress output. Errors are still reported.

.EXAMPLE
    .\Install-PrintPacer.ps1
    .\Install-PrintPacer.ps1 -DesktopShortcut
    .\Install-PrintPacer.ps1 -InstallDir D:\Apps\PrintPacer
    .\Install-PrintPacer.ps1 -DataDir D:\PrintPacer-data
    .\Install-PrintPacer.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [string]$DataDir,
    [switch]$Uninstall,
    [switch]$RemoveData,
    [switch]$DesktopShortcut,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$AppName      = 'PrintPacer'
$Publisher    = 'PrintPacer'
$Version      = '1.8.0'
$DefaultRoot  = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$DefaultData  = Join-Path $env:LOCALAPPDATA $AppName
$DataKey      = "HKCU:\Software\$AppName"
$ManifestName = 'install-manifest.txt'
$StartMenu    = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$DesktopDir   = [Environment]::GetFolderPath('Desktop')
$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"
$RunKey       = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Say($text, $colour = 'Gray') {
    if (-not $Quiet) { Write-Host $text -ForegroundColor $colour }
}

function Stop-RunningApp {
    $running = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    if (-not $running) { return }

    Say "  $AppName is running; closing it first." 'Yellow'
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

function New-Shortcut($path, $target, $description) {
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($path)
        $link.TargetPath       = $target
        $link.WorkingDirectory = Split-Path $target
        $link.Description      = $description
        $link.IconLocation     = "$target,0"
        $link.Save()
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}

function Get-FullPath($path) {
    $full = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($path.Trim().Trim('"')))
    if ($full.Length -gt 3) { $full = $full.TrimEnd('\') }
    return $full
}

# True when $child is $parent itself or anywhere beneath it.
function Test-Within($child, $parent) {
    $c = (Get-FullPath $child).TrimEnd('\') + '\'
    $p = (Get-FullPath $parent).TrimEnd('\') + '\'
    return $c.StartsWith($p, [StringComparison]::OrdinalIgnoreCase)
}

function Get-PreviousRoot {
    $previous = (Get-ItemProperty $UninstallKey -ErrorAction SilentlyContinue).InstallLocation
    if ($previous) { return Get-FullPath $previous }
    return $null
}

# Where the jobs, settings and logs are now. Only PrintPacer writes DataFolder.
function Get-CurrentDataRoot {
    $current = (Get-ItemProperty $DataKey -ErrorAction SilentlyContinue).DataFolder
    if ($current) { return Get-FullPath $current }
    return $DefaultData
}

function Get-PendingDataRoot {
    $pending = (Get-ItemProperty $DataKey -ErrorAction SilentlyContinue).PendingDataFolder
    if ($pending) { return Get-FullPath $pending }
    return $null
}

function Test-HoldsData($folder) {
    (Test-Path -LiteralPath (Join-Path $folder 'jobs.db')) -or (Test-Path -LiteralPath (Join-Path $folder 'settings.json'))
}

# Can files be created there? Tested on the folder itself, or on the nearest
# folder above it that exists, since the install creates the rest.
function Test-Writable($path) {
    $existing = $path
    while (-not (Test-Path -LiteralPath $existing -PathType Container)) {
        $parent = Split-Path $existing -Parent
        if (-not $parent) { return $false }
        $existing = $parent
    }
    $probe = Join-Path $existing ".printpacer-setup-write-test-$PID"
    try {
        [IO.File]::WriteAllText($probe, '')
        Remove-Item -LiteralPath $probe -Force
        return $true
    }
    catch { return $false }
}

# Checks a chosen folder, and returns the folder to install into, or $null with
# the reason already shown. Removing PrintPacer removes its folder, so the folder
# must be PrintPacer's own: a drive root or a folder already holding other files
# is offered a PrintPacer subfolder instead.
function Resolve-InstallRoot($candidate, [bool]$canAsk) {
    try { $dir = Get-FullPath $candidate }
    catch { Write-Warning "'$candidate' is not a valid folder path."; return $null }

    while ($true) {
        if (Test-Within $dir $env:windir) {
            Write-Warning 'PrintPacer cannot be installed inside the Windows folder.'
            return $null
        }
        if (Test-Within $dir (Get-CurrentDataRoot)) {
            Write-Warning "$dir is where PrintPacer keeps its jobs, settings and logs. The program has to go somewhere else."
            return $null
        }

        $isRoot = $dir.Length -le 3
        $isForeign = (Test-Path -LiteralPath $dir -PathType Container) -and
                     (Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue | Select-Object -First 1) -and
                     -not (Test-Path -LiteralPath (Join-Path $dir "$AppName.exe"))
        if (-not ($isRoot -or $isForeign)) { break }

        $suggested = Join-Path $dir $AppName
        if ($canAsk) {
            Say ''
            Say "  $dir is not empty. PrintPacer should have a folder of its own," 'Yellow'
            Say '  so that removing it later removes only PrintPacer.' 'Yellow'
            $answer = Read-Host "  Install into $suggested instead? [Y/n]"
            if ($answer -and $answer.Trim() -notmatch '^(y|yes)$') { return $null }
        }
        else {
            Say "  $dir is not empty; installing into $suggested instead." 'Yellow'
        }
        $dir = $suggested
    }

    if (-not (Test-Writable $dir)) {
        Write-Warning "You cannot write to $dir. Choose a folder in your own profile, such as $DefaultRoot."
        return $null
    }
    return $dir
}

function Select-FolderDialog($startIn, [switch]$ForData) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = if ($ForData) { "Choose where $AppName keeps its jobs, settings and logs." }
                          else { "Choose where to install $AppName. A $AppName folder is created inside the folder you pick." }
    $dialog.ShowNewFolderButton = $true
    $existing = $startIn
    while ($existing -and -not (Test-Path -LiteralPath $existing -PathType Container)) { $existing = Split-Path $existing -Parent }
    if ($existing) { $dialog.SelectedPath = $existing }
    # A topmost owner, or the dialog can open hidden behind the console window.
    $owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true }
    try {
        if ($dialog.ShowDialog($owner) -ne [System.Windows.Forms.DialogResult]::OK) { return $null }
        # A data folder is checked as picked: Resolve-DataRoot adds a subfolder if needed.
        if ($ForData) { return $dialog.SelectedPath }
        # As setup.exe does: browsing picks the parent, PrintPacer goes inside it,
        # unless the folder picked already is a PrintPacer install.
        $picked = $dialog.SelectedPath
        if ((Split-Path $picked -Leaf) -eq $AppName -or (Test-Path -LiteralPath (Join-Path $picked "$AppName.exe"))) { return $picked }
        return Join-Path $picked $AppName
    }
    finally { $dialog.Dispose(); $owner.Dispose() }
}

# Asks where to install, before anything is copied. Enter accepts the suggestion.
function Read-InstallRoot($suggested) {
    while ($true) {
        Say ''
        Say "Where should $AppName be installed?" 'Cyan'
        Say "  Suggested : $suggested"
        Say '  This is for the program only. You choose where your jobs, settings and logs go next.'
        Say ''
        $answer = Read-Host '  Press Enter to use it, B to browse for a folder, or type another folder'
        $answer = if ($answer) { $answer.Trim() } else { '' }

        $candidate = switch -Regex ($answer) {
            '^$'    { $suggested }
            '^[bB]$' { Select-FolderDialog $suggested }
            default { $answer }
        }
        if (-not $candidate) { continue }

        $root = Resolve-InstallRoot $candidate $true
        if ($root) { return $root }
    }
}

# Checks a folder chosen for the data, and returns the folder to use, or $null
# with the reason already shown. The rules match PrintPacer's own
# (DataFolder.Check), which checks again before moving anything there.
function Resolve-DataRoot($candidate, [bool]$canAsk, $installRoot) {
    try { $dir = Get-FullPath $candidate }
    catch { Write-Warning "'$candidate' is not a valid folder path."; return $null }

    $onNetwork = $dir.StartsWith('\\')
    if (-not $onNetwork) {
        try { $onNetwork = ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($dir))).DriveType -eq 'Network' } catch { }
    }
    if ($onNetwork) {
        Write-Warning 'The data folder has to be on this computer. The job database cannot be kept safely on a network drive.'
        return $null
    }

    # A drive root, or a folder holding other things, gets a folder of its own -
    # unless it already holds PrintPacer data, which is then used as it is.
    $isRoot = $dir.Length -le 3
    $isForeign = (Test-Path -LiteralPath $dir -PathType Container) -and
                 (Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue | Select-Object -First 1) -and
                 -not (Test-HoldsData $dir)
    if ($isRoot -or $isForeign) {
        $suggested = Join-Path $dir $AppName
        if ($canAsk) {
            Say ''
            Say "  $dir already holds other things. PrintPacer should keep its data in a folder of its own." 'Yellow'
            $answer = Read-Host "  Use $suggested instead? [Y/n]"
            if ($answer -and $answer.Trim() -notmatch '^(y|yes)$') { return $null }
        }
        else {
            Say "  $dir already holds other things; using $suggested instead." 'Yellow'
        }
        $dir = $suggested
    }

    foreach ($protected in @($env:windir, $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($protected -and (Test-Within $dir $protected)) {
            Write-Warning "PrintPacer cannot keep its data in $protected`: Windows does not let it write there without administrator rights."
            return $null
        }
    }
    if ((Test-Within $dir $installRoot) -or (Test-Within $installRoot $dir)) {
        Write-Warning "Please keep the data apart from the program folder ($installRoot), so that updating or removing the program can never touch your jobs."
        return $null
    }
    $current = Get-CurrentDataRoot
    if (($dir -ne $current) -and ((Test-Within $dir $current) -or (Test-Within $current $dir))) {
        Write-Warning "The new data folder cannot be inside the current one ($current), or the other way round."
        return $null
    }
    if (-not (Test-Writable $dir)) {
        Write-Warning "You cannot write to $dir. Please choose another folder."
        return $null
    }
    return $dir
}

# Asks where to keep the jobs, settings and logs. Enter keeps the suggestion.
function Read-DataRoot($suggested, $installRoot) {
    $current = Get-CurrentDataRoot
    while ($true) {
        Say ''
        Say "Where should $AppName keep your jobs, settings and logs?" 'Cyan'
        Say "  Suggested : $suggested"
        if (Test-HoldsData $current) {
            Say "  Your jobs are in $current now. If you choose another folder, PrintPacer moves them there"
            Say '  the next time it starts, copying and checking every file before removing the old ones.'
        }
        Say ''
        $answer = Read-Host '  Press Enter to use it, B to browse for a folder, or type another folder'
        $answer = if ($answer) { $answer.Trim() } else { '' }

        $candidate = switch -Regex ($answer) {
            '^$'    { $suggested }
            '^[bB]$' { Select-FolderDialog $suggested -ForData }
            default { $answer }
        }
        if (-not $candidate) { continue }

        $root = Resolve-DataRoot $candidate $true $installRoot
        if ($root) { return $root }
    }
}

# Deletes PrintPacer's own data files from $root, and the folder if that leaves
# it empty. Anything else someone keeps there survives.
function Remove-DataFiles($root) {
    if (-not (Test-Path -LiteralPath $root)) { return }
    foreach ($pattern in 'jobs.db', 'jobs.db-*', 'settings.json', 'settings.json.*') {
        Get-ChildItem -LiteralPath $root -Filter $pattern -File -Force -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }
    $logs = Join-Path $root 'logs'
    if (Test-Path -LiteralPath $logs) {
        Get-ChildItem -LiteralPath $logs -Filter 'printpacer-*.log' -File -Force | Remove-Item -Force
        if (-not (Get-ChildItem -LiteralPath $logs -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $logs -Force }
    }
    if (-not (Get-ChildItem -LiteralPath $root -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $root -Force }
}

# Removes the files a previous install put in $root, and the folder if that
# leaves it empty. Only files listed in the install manifest are removed, so
# anything else someone keeps there survives. An install made before manifests
# existed always went to the default folder, created by this script alone, and
# is removed whole as it always was.
function Remove-AppFiles($root) {
    if (-not (Test-Path -LiteralPath $root)) { return }

    $manifest = Join-Path $root $ManifestName
    if (-not (Test-Path -LiteralPath $manifest)) {
        if ((Get-FullPath $root) -eq (Get-FullPath $DefaultRoot) -and (Test-Path -LiteralPath (Join-Path $root "$AppName.exe"))) {
            Remove-Item -LiteralPath $root -Recurse -Force
        }
        else {
            Write-Warning "$root has no install manifest, so it was left in place. Delete it by hand if it is no longer needed."
        }
        return
    }

    foreach ($relative in Get-Content -LiteralPath $manifest) {
        if (-not $relative -or $relative.Contains('..') -or [IO.Path]::IsPathRooted($relative)) { continue }
        $file = Join-Path $root $relative
        if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file -Force }
    }
    Remove-Item -LiteralPath $manifest -Force

    # Deepest first, so a folder emptied by its children is removed in turn.
    Get-ChildItem -LiteralPath $root -Directory -Recurse -Force |
        Sort-Object { $_.FullName.Length } -Descending |
        Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force | Select-Object -First 1) } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

    if (Get-ChildItem -LiteralPath $root -Force | Select-Object -First 1) {
        Say "  left $root in place: it holds files $AppName did not install" 'Yellow'
    }
    else {
        Remove-Item -LiteralPath $root -Force
    }
}

# ===========================================================================
#  Uninstall
# ===========================================================================
if ($Uninstall) {
    # The folder was the user's choice: the copy of this script inside it knows
    # where it is, and the Installed apps entry records it otherwise.
    $InstallRoot = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot "$AppName.exe")) { $PSScriptRoot }
                   elseif (Get-PreviousRoot) { Get-PreviousRoot }
                   else { $DefaultRoot }

    Say "Removing $AppName from $InstallRoot" 'Cyan'
    Stop-RunningApp

    # Drop the "start with Windows" entry, or the next sign-in fails silently.
    try {
        $run = Get-ItemProperty $RunKey -ErrorAction SilentlyContinue
        if ($run -and $run.PSObject.Properties.Name -contains $AppName) {
            Remove-ItemProperty $RunKey -Name $AppName -ErrorAction SilentlyContinue
            Say '  removed the startup entry'
        }
    } catch { Write-Warning "Could not remove the startup entry: $($_.Exception.Message)" }

    foreach ($shortcut in @((Join-Path $StartMenu "$AppName.lnk"), (Join-Path $DesktopDir "$AppName.lnk"))) {
        if (Test-Path $shortcut) { Remove-Item $shortcut -Force; Say "  removed $shortcut" }
    }

    if (Test-Path $UninstallKey) { Remove-Item $UninstallKey -Recurse -Force; Say '  removed the Installed apps entry' }

    if (Test-Path -LiteralPath $InstallRoot) {
        Remove-AppFiles $InstallRoot
        Say "  removed the program from $InstallRoot"
    }

    $dataRoot = Get-CurrentDataRoot
    if ($RemoveData) {
        if (Test-Path -LiteralPath $dataRoot) {
            Remove-DataFiles $dataRoot
            Say "  removed the jobs, settings and logs from $dataRoot" 'Yellow'
        }
        # With the data gone, so is the record of where it was.
        if (Test-Path $DataKey) { Remove-Item $DataKey -Recurse -Force }
    }
    elseif (Test-Path -LiteralPath $dataRoot) {
        # The record of the data folder stays too, so a reinstall finds the jobs again.
        Say ''
        Say "Your jobs, settings and logs were kept in:" 'Yellow'
        Say "  $dataRoot"
        Say "Delete that folder by hand, or re-run with -Uninstall -RemoveData, to remove them too."
    }

    Say ''
    Say "$AppName has been removed." 'Green'
    return
}

# ===========================================================================
#  Install
# ===========================================================================
$payload = Join-Path $PSScriptRoot $AppName
if (-not (Test-Path (Join-Path $payload "$AppName.exe"))) {
    throw @"
Could not find $AppName.exe in:
  $payload

Run this script from the publish folder produced by build.ps1, which contains
a '$AppName' subfolder next to this script.
"@
}

Say "Installing $AppName $Version" 'Cyan'

# ---- Choose the folder, before anything is touched -------------------------
$PreviousRoot = Get-PreviousRoot
$suggested    = if ($PreviousRoot) { $PreviousRoot } else { $DefaultRoot }
$canAsk       = -not $Quiet -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected

if ($InstallDir) {
    $InstallRoot = Resolve-InstallRoot $InstallDir $false
    if (-not $InstallRoot) { throw "Cannot install into $InstallDir." }
}
elseif ($canAsk) {
    $InstallRoot = Read-InstallRoot $suggested
}
else {
    $InstallRoot = Resolve-InstallRoot $suggested $false
    if (-not $InstallRoot) { throw "Cannot install into $suggested. Pass -InstallDir to choose another folder." }
}

$moving = $PreviousRoot -and ($PreviousRoot -ne $InstallRoot) -and
          (Test-Path -LiteralPath (Join-Path $PreviousRoot "$AppName.exe"))

# ---- Choose the data folder ------------------------------------------------
$CurrentData   = Get-CurrentDataRoot
$suggestedData = if (Get-PendingDataRoot) { Get-PendingDataRoot } else { $CurrentData }

if ($DataDir) {
    $DataRoot = Resolve-DataRoot $DataDir $false $InstallRoot
    if (-not $DataRoot) { throw "Cannot keep the data in $DataDir." }
}
elseif ($canAsk) {
    $DataRoot = Read-DataRoot $suggestedData $InstallRoot
}
else {
    $DataRoot = $suggestedData
}
$movingData = $DataRoot -ne $CurrentData

Say ''
Say "  from : $payload"
Say "  to   : $InstallRoot"
if ($moving) { Say "  (moving from $PreviousRoot, which is removed afterwards)" }
Say "  data : $DataRoot"
if ($movingData -and (Test-HoldsData $CurrentData)) { Say "  (moved from $CurrentData when $AppName next starts)" }

Stop-RunningApp

# Replace the program files, but never the data directory: an upgrade in the
# middle of a multi-hour print job must not lose the job.
if (Test-Path -LiteralPath $InstallRoot) {
    Say '  removing the previous version'
    Remove-AppFiles $InstallRoot
}

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
Copy-Item (Join-Path $payload '*') $InstallRoot -Recurse -Force

# Record exactly what was installed, so upgrading, moving and uninstalling
# remove these files and nothing else that may share the folder.
$payloadFull = (Get-Item -LiteralPath $payload).FullName.TrimEnd('\')
$installed = @(Get-ChildItem -LiteralPath $payload -Recurse -File |
               ForEach-Object { $_.FullName.Substring($payloadFull.Length + 1) }) + 'Install-PrintPacer.ps1'
Set-Content -LiteralPath (Join-Path $InstallRoot $ManifestName) -Value $installed -Encoding UTF8

$exe = Join-Path $InstallRoot "$AppName.exe"
if (-not (Test-Path $exe)) { throw "The copy finished but $exe is missing." }

$fileCount = (Get-ChildItem $InstallRoot -Recurse -File).Count
$sizeMb = [math]::Round((Get-ChildItem $InstallRoot -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Say "  copied $fileCount files ($sizeMb MB)"

# ---- Shortcuts -------------------------------------------------------------
$description = 'Print large PDFs in paced batches, resting the printer in between'

New-Shortcut (Join-Path $StartMenu "$AppName.lnk") $exe $description
Say "  Start Menu shortcut created"

if ($DesktopShortcut) {
    New-Shortcut (Join-Path $DesktopDir "$AppName.lnk") $exe $description
    Say '  Desktop shortcut created'
}

# ---- Installed apps entry --------------------------------------------------
# Registered under HKCU so it appears in Settings > Apps without elevation.
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallRoot\Install-PrintPacer.ps1`" -Uninstall"

New-Item -Path $UninstallKey -Force | Out-Null
$values = @{
    DisplayName     = $AppName
    DisplayVersion  = $Version
    Publisher       = $Publisher
    DisplayIcon     = $exe
    InstallLocation = $InstallRoot
    UninstallString = $uninstallCommand
    QuietUninstallString = "$uninstallCommand -Quiet"
    NoModify        = 1
    NoRepair        = 1
    EstimatedSize   = [int]((Get-ChildItem $InstallRoot -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    InstallDate     = (Get-Date -Format 'yyyyMMdd')
}
foreach ($name in $values.Keys) {
    $kind = if ($values[$name] -is [int]) { 'DWord' } else { 'String' }
    New-ItemProperty -Path $UninstallKey -Name $name -Value $values[$name] -PropertyType $kind -Force | Out-Null
}
Say '  registered in Settings > Apps > Installed apps'

# Keep a copy of this script inside the install so uninstall works later.
Copy-Item $PSCommandPath (Join-Path $InstallRoot 'Install-PrintPacer.ps1') -Force

# ---- Data folder -----------------------------------------------------------
# Recorded only. PrintPacer moves the data itself when it next starts, while
# nothing has the job database open, and keeps the old folder if it cannot.
if ($movingData) {
    New-Item -Path $DataKey -Force | Out-Null
    New-ItemProperty -Path $DataKey -Name 'PendingDataFolder' -Value $DataRoot -PropertyType String -Force | Out-Null
    Say "  data folder set to $DataRoot"
}
elseif (Get-PendingDataRoot) {
    Remove-ItemProperty -Path $DataKey -Name 'PendingDataFolder' -ErrorAction SilentlyContinue
}

# Moved to a new folder: the shortcuts and the Installed apps entry now point
# there, so the old copy can go.
if ($moving) {
    Remove-AppFiles $PreviousRoot
    Say "  removed the old copy from $PreviousRoot"
}

Say ''
Say "$AppName is installed." 'Green'
Say ''
Say "  Start it from : the Start Menu, or $exe"
Say "  Your data in  : $DataRoot$(if ($movingData -and (Test-HoldsData $CurrentData)) { ' (moved there when PrintPacer next starts)' })"
Say "  To remove it  : Settings > Apps > Installed apps > $AppName"
Say ''
