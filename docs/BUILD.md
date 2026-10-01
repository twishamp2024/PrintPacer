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

### The setup.exe (what end users get)

```powershell
winget install JRSoftware.InnoSetup
.\build.ps1 -Installer
```

Produces `installer\output\PrintPacer-Setup-1.7.0.exe` — a single 51 MB file
compressed from the 157 MB payload, which a user downloads and double-clicks.

Installs for all users, with a Start Menu shortcut, an optional Desktop
shortcut, an optional start-with-Windows entry, an **Open with → PrintPacer**
verb on PDFs (a verb only — the default PDF reader is untouched), and an
uninstaller in **Settings → Apps**.

**The install folder is the user's choice.** The *Select Destination Location*
page is always shown, upgrades included, pre-filled with
`C:\Program Files\PrintPacer` or wherever the previous version went. Choosing a
different folder on an upgrade moves the install: the old copy is removed once
the new one is in place, and the shortcuts, startup entry and Open with verb
follow it. Before anything is copied, setup checks the folder:

| Chosen folder | What setup does |
|---|---|
| A drive root, or a folder already holding other files | Offers `<folder>\PrintPacer` instead |
| The data folder, `%LOCALAPPDATA%\PrintPacer` | Refuses — updating the program must never touch the jobs |
| Inside the Windows folder | Refuses |
| Somewhere the installing user cannot write | Refuses, and says why |

The folder has to be PrintPacer's own because the uninstaller removes it. It
removes only the files setup installed, and the folder only if that leaves it
empty, so anything else someone keeps there survives.

**The data folder is the user's choice too.** The next page, *Choose where to
keep your jobs*, is pre-filled with the folder in use (`%LOCALAPPDATA%\PrintPacer`
unless it was changed). Setup never moves data itself: a different folder is
recorded in `HKCU\Software\PrintPacer` as `PendingDataFolder`, and PrintPacer
moves the jobs, settings and logs when it next starts, before it opens anything.
It copies every file, checks each copy byte for byte, records the new folder as
`DataFolder`, and only then deletes the originals. A failure before that point
removes the partial copy and keeps the old folder in use. A folder that already
holds PrintPacer data is used as it is, with nothing copied over it.

| Chosen data folder | What setup and PrintPacer do |
|---|---|
| A drive root, or a folder already holding other things | Offer `<folder>\PrintPacer` instead |
| A folder already holding PrintPacer data | Use it as it is |
| A network share or mapped network drive | Refuse — SQLite's write-ahead log is not safe there |
| Program Files or the Windows folder | Refuse — a standard user cannot write there |
| Inside the program folder, or containing it | Refuse |
| Inside the current data folder, or containing it | Refuse |

PrintPacer checks the folder again at startup, as the user, because an elevated
setup cannot tell what the user may write to. The choice can also be changed
later in **Settings → Storage → Change…**. The Start Menu's *PrintPacer data
folder* shortcut runs `PrintPacer.exe --open-data-folder`, which opens whatever
the folder is now.

Installing needs administrator approval because it writes to Program Files.
Running the application afterwards does not: PrintPacer needs no elevated
privileges to read print queues, submit jobs or cancel its own jobs, and its
data folder is per user. A user who cannot elevate is offered a per-user
install instead.

Useful switches:

```powershell
PrintPacer-Setup-1.7.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
PrintPacer-Setup-1.7.0.exe /VERYSILENT /TASKS=desktopicon
PrintPacer-Setup-1.7.0.exe /VERYSILENT /DIR="D:\Apps\PrintPacer"
PrintPacer-Setup-1.7.0.exe /VERYSILENT /DATADIR="D:\PrintPacer data"
PrintPacer-Setup-1.7.0.exe /LOG=setup.log
```

The setup declares `AppMutex`, so if PrintPacer is running when an upgrade
starts, the user is asked to close it rather than the install failing on a
locked file.

### Without any extra tooling

If you would rather not install Inno Setup, or want an install with no
administrator step at all:

```powershell
.\publish\Install-PrintPacer.cmd
```

Per-user install, Start Menu shortcut, and an entry in **Settings → Apps →
Installed apps**. Before copying anything it asks where to install — press
Enter for `%LOCALAPPDATA%\Programs\PrintPacer` (or the previous install's
folder), **B** to browse for one, or type a path — and then, the same way,
where to keep the jobs, settings and logs. The same checks as setup.exe apply,
and a different data folder is recorded for PrintPacer to move to, exactly as
setup.exe does. It records the program files it installed in
`install-manifest.txt`, so an upgrade, a move or an uninstall removes those
files and nothing else.

```powershell
.\Install-PrintPacer.ps1 -InstallDir D:\Apps\PrintPacer   # no question asked
.\Install-PrintPacer.ps1 -DataDir "D:\PrintPacer data"     # no question asked
.\Install-PrintPacer.ps1 -DesktopShortcut
.\Install-PrintPacer.ps1 -Uninstall
.\Install-PrintPacer.ps1 -Uninstall -RemoveData   # also deletes jobs, settings, logs
```

`-RemoveData` deletes PrintPacer's own files from the data folder, wherever it
is, and the folder only if that leaves it empty.

Both paths leave the data folder alone on upgrade and on uninstall, so
upgrading mid-job does not lose the job and the logs survive removal. The
record of where it is (`HKCU\Software\PrintPacer`) stays too, so a reinstall
finds the jobs again.

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
├── tests/PrintPacer.Tests/      252 tests
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
& $probe png printed.pdf pngs 60             # render each page to a PNG, to look at a layout
```

To try a build without touching the jobs of the copy in real use - which it would
otherwise open, migrate and, with automatic resume on, carry on printing - give it
its own data folder:

```powershell
.\src\PrintPacer.App\bin\Debug\net8.0-windows\PrintPacer.exe --data-dir=C:\Temp\pp-sandbox
```

`--data-dir` also ignores the user's chosen data folder and any pending move, and
hides **Change…** in Settings, so a sandbox can never move or redirect real data.

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
