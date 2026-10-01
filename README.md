# PrintPacer

Print a large PDF as a series of **independent Windows print jobs**, with a rest
period between each one.

Built for a specific, annoying problem: an inkjet that stalls after 30–50
continuous pages and needs to sit idle for half an hour before it will carry on.
Sending a 200-page PDF as one job guarantees a stall. Sending it as four 50-page
jobs an hour apart does not.

```
textbook.pdf, 200 pages, 50 per batch, 60 minutes apart

  Batch 1   Pages 1–50      23:00
  Batch 2   Pages 51–100    00:00
  Batch 3   Pages 101–150   01:00
  Batch 4   Pages 151–200   02:00
```

Set it up before bed, close the lid, and collect the document in the morning.

Or queue a whole day's work: drop many PDFs at once and each becomes its own job,
with its own printer, colour, sides, batch size and rest. They print one after
another overnight, in the order you choose, and a problem with one customer's file
never touches the others.

---

## The one thing this application is really about

Not printing anything twice.

Reprinting fifty pages because the software lost track is worse than not
printing them at all — and it is exactly what happens if an application assumes
that "the job vanished from the queue" means "the job printed".

PrintPacer therefore never guesses. It watches two independent sources:

| Source | Answers |
|---|---|
| The Windows print spooler | What Windows did with the job |
| The printer itself, over IPP and HP LEDM | How many sheets physically came out |

The second one is the interesting part. Most network printers expose a lifetime
page counter. Reading it before and after a batch gives **physical, hardware-level
proof** of how many sheets were marked — which survives the application crashing,
the PC restarting, or the power going out mid-batch.

A batch is only ever resubmitted automatically when the evidence positively shows
that **nothing** printed. Every other outcome stops and asks you, and says exactly
how far the batch got:

> The printer produced 18 of 50 sheets, so the batch stopped part-way. Check the
> output tray: the pages after the first 18 of this batch are the ones still needed.

You can then keep the 18 that printed and send only the remaining 32.

If your printer has no reachable management interface, everything still works —
PrintPacer just asks you to confirm more often, which is the honest trade.

---

## Installing

**Requirements:** Windows 10 or 11, 64-bit. Nothing else — the build is
self-contained, so .NET does not need to be installed.

Double-click **`PrintPacer-Setup-1.4.0.exe`** (51 MB). Before anything is
copied it asks where to install — `C:\Program Files\PrintPacer` unless you
choose another folder or drive — then adds a Start Menu shortcut, offers a
Desktop shortcut, and registers a proper uninstaller in **Settings → Apps**.
Running setup again lets you move an existing install; the old copy is removed.

Installing needs administrator approval, as anything writing to Program Files
does. **Running** PrintPacer afterwards does not: it works entirely as a
standard user, keeping its jobs, settings and logs in your own profile. A user
who cannot elevate is offered a per-user install instead.

To build the installer yourself:

```powershell
winget install JRSoftware.InnoSetup
.\build.ps1 -Installer
```

See [docs/BUILD.md](docs/BUILD.md) for the full details, including a per-user
install that needs no administrator rights at all.

---

## Using it

1. Drop one or more PDFs onto the window (or use **Browse**, or right-click →
   **Open with → PrintPacer**). Each becomes its own job in the print queue;
   click a job to see or change its settings.
2. Choose what to print — all, odd or even pages; a normal layout, a booklet or
   a brochure; single- or double-sided — then check the printer, print mode
   (colour or black & white), pages (or sheets) per batch, and rest period.
3. Read **What will happen** at the bottom of the job's settings — the plan in
   three lines, with every batch and its timing under **View schedule** —
   before anything is committed.
4. Order the queue with ▲ ▼ and press **Start printing** under the queue. Jobs print one after
   another, each with its own settings, with the printer resting between batches
   and between jobs.

Closing the window does **not** stop the queue; it keeps running from the
notification area. Exiting properly asks first if a job is still going.

The full walkthrough, including what to do when something goes wrong overnight,
is in [docs/USER-GUIDE.md](docs/USER-GUIDE.md).

---

## Documentation

| Document | What it covers |
|---|---|
| [USER-GUIDE.md](docs/USER-GUIDE.md) | Day-to-day use, every screen, every recovery action |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | How it works and why it is built this way |
| [BUILD.md](docs/BUILD.md) | Building, publishing, packaging, project layout |
| [LIMITATIONS.md](docs/LIMITATIONS.md) | What it cannot do, and what it deliberately refuses to pretend |
| [TESTING.md](docs/TESTING.md) | Test strategy, results, and how to run a real overnight test |
| [REVIEW.md](docs/REVIEW.md) | Critical self-review: every failure mode, what was found, what was fixed |

---

## Project layout

```
PrintPacer.sln
├── src/
│   ├── PrintPacer.Core/     Batching, scheduling, persistence, printer monitoring
│   └── PrintPacer.App/      WPF interface, tray presence, power management
├── tests/
│   └── PrintPacer.Tests/    100 tests against the real engine, a simulated printer and the Windows spooler
├── tools/
│   └── PrintPacer.Probe/    Hardware diagnostics and test-fixture generation
├── installer/               Installer scripts
└── docs/                    The documents listed above
```

---

## Privacy

Everything is local. PDFs are read from where they already are and never copied,
uploaded or sent anywhere. There is no telemetry, no analytics and no update
check. The only network traffic is to your own printer on your own network, and
only to read its status and page counter — the app never writes a setting to it.

Your data lives in `%LOCALAPPDATA%\PrintPacer`:

```
jobs.db          Job, batch and attempt history (SQLite)
settings.json    Preferences
logs\            Rolling daily logs, kept 30 days by default
```

Uninstalling leaves that folder in place, so a log is still available afterwards.

---

## Licence and dependencies

| Dependency | Used for | Licence |
|---|---|---|
| [PDFium](https://pdfium.googlesource.com/pdfium/) via [Docnet.Core](https://github.com/GowenGit/docnet) | Page count and rendering | BSD-3-Clause |
| [PDFsharp](https://github.com/empira/PDFsharp) | Document metadata | MIT |
| [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) | Durable job state | MIT |
| [Serilog](https://serilog.net/) | Structured logging | Apache-2.0 |
| [xUnit](https://xunit.net/) + [FluentAssertions](https://fluentassertions.com/) | Tests (not shipped) | Apache-2.0 / Apache-2.0 |

Why each one was chosen is explained in
[ARCHITECTURE.md](docs/ARCHITECTURE.md#dependencies).
