<#
.SYNOPSIS
    Builds, tests and publishes PrintPacer.

.DESCRIPTION
    Produces a self-contained x64 build under publish\PrintPacer that runs on a
    machine with no .NET installed, then optionally compiles the Inno Setup
    installer if Inno Setup is available.

    Self-contained rather than framework-dependent, and a folder rather than a
    single file: the application loads two native libraries (PDFium for page
    rendering and SQLite for the job database), and a folder deployment loads
    them directly instead of extracting them to a temporary directory on every
    launch.

.PARAMETER SkipTests
    Skip the test suite. Not recommended: the tests are the only thing standing
    between a change and a duplicate stack of paper.

.PARAMETER Installer
    Also compile installer\PrintPacer.iss with Inno Setup, if it is installed.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Installer
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publishDir = Join-Path $root 'publish\PrintPacer'

# Keep the build off the network telemetry path and make it reproducible.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Write-Step($text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

# ---- Locate the SDK --------------------------------------------------------
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -and (Test-Path 'C:\Program Files\dotnet\dotnet.exe')) {
    $dotnet = 'C:\Program Files\dotnet\dotnet.exe'
    $env:Path = 'C:\Program Files\dotnet;' + $env:Path
}
if (-not $dotnet) {
    throw "The .NET SDK was not found. Install .NET 8 or later:  winget install Microsoft.DotNet.SDK.8"
}

Write-Step "Using $dotnet ($(& $dotnet --version))"

# ---- Build -----------------------------------------------------------------
Write-Step 'Building'
& $dotnet build (Join-Path $root 'PrintPacer.sln') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# ---- Test ------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Step 'Running tests'
    & $dotnet test (Join-Path $root 'tests\PrintPacer.Tests\PrintPacer.Tests.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Nothing has been published.' }
}

# ---- Publish ---------------------------------------------------------------
Write-Step "Publishing to $publishDir"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

& $dotnet publish (Join-Path $root 'src\PrintPacer.App\PrintPacer.App.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:DebugType=none `
    -o $publishDir `
    --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$exe = Join-Path $publishDir 'PrintPacer.exe'
if (-not (Test-Path $exe)) { throw "Publish completed but $exe is missing." }

# Sanity-check that the native dependencies actually came across. Their absence
# would only show up as a crash the first time a user opened a PDF.
foreach ($native in @('pdfium.dll', 'e_sqlite3.dll')) {
    if (-not (Get-ChildItem $publishDir -Recurse -Filter $native -ErrorAction SilentlyContinue)) {
        throw "$native is missing from the publish output; the application would fail at run time."
    }
}

$size = [math]::Round((Get-ChildItem $publishDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host "    published $((Get-ChildItem $publishDir -Recurse -File).Count) files, $size MB" -ForegroundColor Green

# Copy the installer scripts next to the payload so the folder is self-sufficient.
Copy-Item (Join-Path $root 'installer\Install-PrintPacer.ps1') (Split-Path $publishDir) -Force
Copy-Item (Join-Path $root 'installer\Install-PrintPacer.cmd') (Split-Path $publishDir) -Force

# ---- Installer -------------------------------------------------------------
if ($Installer) {
    Write-Step 'Compiling the Inno Setup installer'

    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) { $iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source }

    if (-not $iscc) {
        Write-Warning @'
Inno Setup was not found, so setup.exe was not built.

Install it with:    winget install JRSoftware.InnoSetup
then run:           .\build.ps1 -Installer

The folder in publish\ is already complete. Install-PrintPacer.cmd next to it
installs PrintPacer without needing Inno Setup or administrator rights.
'@
    }
    else {
        & $iscc (Join-Path $root 'installer\PrintPacer.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
        Write-Host "    setup written to installer\output" -ForegroundColor Green
    }
}

Write-Step 'Done'
Write-Host "  Application : $exe"
Write-Host "  Install with: publish\Install-PrintPacer.cmd"
Write-Host ''
