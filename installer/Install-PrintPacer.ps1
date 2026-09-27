<#
.SYNOPSIS
    Installs or removes PrintPacer for the current user.

.DESCRIPTION
    A per-user install: everything goes under %LOCALAPPDATA%, so no administrator
    rights are needed and nothing outside your own profile is touched. PrintPacer
    itself needs no elevated privileges to read print queues, submit jobs or
    cancel its own jobs, so the installer should not demand them either.

    Creates:
      - the application in %LOCALAPPDATA%\Programs\PrintPacer
      - a Start Menu shortcut
      - a Desktop shortcut, if asked for
      - an entry in Settings > Apps > Installed apps, so it uninstalls the
        ordinary way

    Leaves alone (so an upgrade never loses a running job):
      - %LOCALAPPDATA%\PrintPacer, which holds the job database, settings and logs

.PARAMETER Uninstall
    Remove the application, its shortcuts and its Installed apps entry.

.PARAMETER RemoveData
    With -Uninstall, also delete the job database, settings and logs.

.PARAMETER DesktopShortcut
    Also place a shortcut on the Desktop.

.PARAMETER Quiet
    Suppress progress output. Errors are still reported.

.EXAMPLE
    .\Install-PrintPacer.ps1
    .\Install-PrintPacer.ps1 -DesktopShortcut
    .\Install-PrintPacer.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$RemoveData,
    [switch]$DesktopShortcut,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$AppName      = 'PrintPacer'
$Publisher    = 'PrintPacer'
$Version      = '1.3.0'
$InstallRoot  = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$DataRoot     = Join-Path $env:LOCALAPPDATA $AppName
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

# ===========================================================================
#  Uninstall
# ===========================================================================
if ($Uninstall) {
    Say "Removing $AppName" 'Cyan'
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

    if (Test-Path $InstallRoot) {
        Remove-Item $InstallRoot -Recurse -Force
        Say "  removed $InstallRoot"
    }

    if ($RemoveData) {
        if (Test-Path $DataRoot) {
            Remove-Item $DataRoot -Recurse -Force
            Say "  removed $DataRoot (jobs, settings and logs)" 'Yellow'
        }
    }
    elseif (Test-Path $DataRoot) {
        Say ''
        Say "Your jobs, settings and logs were kept in:" 'Yellow'
        Say "  $DataRoot"
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
Say "  from : $payload"
Say "  to   : $InstallRoot"

Stop-RunningApp

# Replace the program files wholesale, but never the data directory: an upgrade
# in the middle of a multi-hour print job must not lose the job.
if (Test-Path $InstallRoot) {
    Say '  removing the previous version'
    Remove-Item $InstallRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
Copy-Item (Join-Path $payload '*') $InstallRoot -Recurse -Force

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

New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null

Say ''
Say "$AppName is installed." 'Green'
Say ''
Say "  Start it from : the Start Menu, or $exe"
Say "  Your data in  : $DataRoot"
Say "  To remove it  : Settings > Apps > Installed apps > $AppName"
Say ''
