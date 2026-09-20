# Building PrintPacer

---

## Requirements

- Windows 10 or 11, 64-bit
- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0) or later

```powershell
winget install Microsoft.DotNet.SDK.8
```

Optional, only for a classic `setup.exe`:

```powershell
winget install JRSoftware.InnoSetup
```

**End users need none of this.** The published build is self-contained.

---

## Build, test and publish

```powershell
.\build.ps1
```

That builds Release, runs the full test suite, and publishes a self-contained
x64 build to `publish\PrintPacer`. The script **stops if any test fails** and
publishes nothing — the tests are the only thing standing between a change and a
duplicate stack of paper.

It also verifies that both native dependencies (`pdfium.dll`, `e_sqlite3.dll`)
reached the output, since their absence would otherwise surface as a crash the
first time a user opened a PDF.

| Option | Effect |
|---|---|
| `-SkipTests` | Skip the suite. Not recommended. |
| `-Installer` | Also compile `installer\PrintPacer.iss`, if Inno Setup is present. |

### Individual commands

```powershell
dotnet build PrintPacer.sln -c Release
dotnet test tests\PrintPacer.Tests\PrintPacer.Tests.csproj
dotnet run --project src\PrintPacer.App\PrintPacer.App.csproj
```

Run with `--verbose` for debug-level logging, including data-binding failures:

```powershell
.\publish\PrintPacer\PrintPacer.exe --verbose
```

---

## Installing

### Without extra tooling

```powershell
.\publish\Install-PrintPacer.cmd
```

Per-user install to `%LOCALAPPDATA%\Programs\PrintPacer`, Start Menu shortcut,
and an entry in **Settings → Apps → Installed apps**. No administrator rights.

```powershell
# Options
.\Install-PrintPacer.ps1 -DesktopShortcut
.\Install-PrintPacer.ps1 -Uninstall
.\Install-PrintPacer.ps1 -Uninstall -RemoveData   # also deletes jobs, settings, logs
```

An upgrade replaces the program files but **never** touches
`%LOCALAPPDATA%\PrintPacer`, so upgrading mid-job does not lose the job.

### As a setup.exe

```powershell
winget install JRSoftware.InnoSetup
.\build.ps1 -Installer
```

Produces `installer\output\PrintPacer-Setup-1.0.0.exe`, with optional desktop
shortcut, optional start-with-Windows, and an **Open with → PrintPacer** verb on
PDFs (registered as a verb, so your usual PDF reader stays the default).

Both installers are per-user by design: PrintPacer needs no elevated privileges
to read print queues, submit jobs or cancel its own jobs, so the installer does
not demand them either.

---

## Project layout

```
PrintPacer.sln
├── Directory.Build.props        Shared settings; pins x64
├── build.ps1                    Build, test, publish, package
│
├── src/PrintPacer.Core/         No UI. Everything that must be correct.
│   ├── Model/                   Job, Batch, BatchAttempt, settings, state enums
│   ├── Spooler/                 winspool.drv interop behind ISpooler
│   ├── Device/                  IPP + HP LEDM clients, address discovery
│   ├── Pdf/                     Document inspection
│   ├── Printing/                IPrintEngine + the PDFium implementation
│   ├── Persistence/             SQLite job store, settings, paths
│   ├── Engine/                  Scheduler, verifier, clock, snapshots
│   ├── Simulation/              Software printer, for testing
│   └── Diagnostics/             Logging setup
│
├── src/PrintPacer.App/          WPF. Projection and input only.
│   ├── ViewModels/              Screen state from engine snapshots
│   ├── Views/                   XAML + code-behind
│   ├── Platform/                Power requests, tray icon, binding diagnostics
│   ├── Themes/                  Styles
│   └── Assets/                  Icon
│
├── tests/PrintPacer.Tests/      70 tests
├── tools/PrintPacer.Probe/      Diagnostics. Not shipped.
├── installer/                   Installer scripts
└── docs/                        Documentation
```

The split is load-bearing: `Core` has no reference to WPF and no knowledge that
a user interface exists. Every rule about what may be reprinted lives there and
is tested there.

---

## The diagnostic tool

`PrintPacer.Probe` exercises the hardware paths directly. Useful when
investigating a printer that behaves oddly.

```powershell
$probe = ".\tools\PrintPacer.Probe\bin\Debug\net8.0-windows\PrintPacer.Probe.exe"

& $probe spooler                             # all printers and queues
& $probe device "HP Ink Tank"                # resolve address, read state + counters
& $probe info textbook.pdf                   # inspect as the app does
& $probe makepdfs tests\fixtures             # generate the fixture PDFs
& $probe makepdf out.pdf 42 "Title"          # one PDF with N pages
& $probe rendercheck in.pdf 1 300            # PDFium pixel format and alpha model
& $probe print in.pdf 51 100 "Printer" out.pdf    # one batch as one spooler job
& $probe verify out.pdf in.pdf 51 100        # which source pages actually printed
& $probe makeicon app.ico                    # regenerate the application icon
```

`verify` is the interesting one: it fingerprints every page of both files and
matches printed pages back to source pages, which works even though the output
is rasterised and has no text layer.

---

## Notes for maintainers

**x64 is pinned** in `Directory.Build.props` via `PlatformTarget`, because PDFium
and SQLite load in-process. `<Platforms>x64</Platforms>` is deliberately *not*
set: it would make solution builds emit to `bin\x64\Debug` while project builds
use `bin\Debug`, leaving two output trees and an easy way to run a stale binary.

**Publish size is ~157 MB** across 266 files. That is self-contained WPF with
ReadyToRun. It can be reduced by dropping `PublishReadyToRun` (smaller, slower
to start) or by going framework-dependent (much smaller, but then .NET 8 must be
installed). Trimming is not viable — WPF does not support it.

**`JobEngine.Jobs` is `internal`** on purpose. The live job objects are only safe
to read from the engine loop. Everything outside uses `GetJobsAsync()` or the
snapshot delivered by `JobChanged`.

**`BatchVerifier` is a pure function.** Keep it that way. It is the component
that decides whether pages may be reprinted, and its testability depends on
having no I/O and no state.

**Adding a state to the engine** means updating `JobState`, the `StateHeadline`
and colour maps in `MainViewModel`, and the recovery logic in `RecoverAsync`.
The compiler will not catch a missed switch arm, because the maps have a
catch-all — search for `JobState.` when adding one.
