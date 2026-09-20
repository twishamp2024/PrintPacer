# Testing

---

## Approach

The engine runs its **real** loop against its **real** SQLite store. Only the
world *outside* the application is simulated — the printer, its queue, and the
clock. A passing test therefore means the shipped code paths work, not that a
parallel implementation does.

Three things made this practical:

**`SimulatedPrinter`** models a printer and its Windows queue, with switches for
failures that are impractical to stage with real hardware: stalling mid-batch, a
queue that cannot be read, a page counter that lags the spooler or freezes
entirely, a printer that vanishes from Windows, jobs that disappear without
printing, and a driver that retains finished jobs in the queue.

**`ManualClock`** makes a twelve-hour delay instant, and distinguishes three
different things that all look like "time passed": normal elapsed time, suspend
(wall clock advances, monotonic does not), and a clock that jumps.

**`TestPdfBuilder`** writes valid, uncompressed PDFs by hand against the base-14
Helvetica font — no embedding, no compression, no library. Every page carries its
own number in very large type, so printed output can be matched back to source
pages.

```powershell
.\build.ps1                 # build, test, publish (stops if any test fails)
dotnet test tests\PrintPacer.Tests\PrintPacer.Tests.csproj
```

---

## Results

```
Passed!  Failed: 0   Passed: 70   Skipped: 0   Total: 70   Duration: 24 s
```

Verified on .NET 8.0.425, Windows 11 24H2 (build 26200), x64, in both Debug and
Release, and run repeatedly to confirm no flakiness.

| Suite | Tests | Covers |
|---|---|---|
| `BatchPlanningTests` | 13 | Page ranges, boundaries, invalid input |
| `BatchVerifierTests` | 15 | The did-it-print decision table |
| `JobEngineTests` | 31 | Scheduling, printer faults, stalls, controls |
| `RecoveryTests` | 11 | Crash, restart, adoption, duplicate avoidance |

---

## The scenarios from the brief

Every case listed in the original requirements, and where it is covered.

| # | Scenario | Covered by |
|---|---|---|
| 1 | 1-page PDF | `ASinglePageDocumentPrints` |
| 2 | 10-page PDF | `BatchSizeOfOneStillPrintsEveryPageExactlyOnce` |
| 3 | 50-page PDF | `ProducesTheExpectedNumberOfBatches(50, 50)` |
| 4 | 51-page PDF | `ProducesTheExpectedNumberOfBatches(51, 50)`, `SinglePageBatchesAreLabelledWithoutARange` |
| 5 | 200-page PDF | `PrintsA200PageDocumentAsFourSeparateJobsWithRestsBetween` |
| 6 | Not divisible by batch size | `APageCountNotDivisibleByTheBatchSizeEndsWithAShortBatch`, `FinalBatchAbsorbsTheRemainder` |
| 7 | Batch larger than document | `BatchSizeLargerThanTheDocumentPrintsOneBatch` |
| 8 | Batch size of 1 | `BatchSizeOfOneStillPrintsEveryPageExactlyOnce` |
| 9 | Very long delay | `AVeryLongDelayIsHonouredExactlyAndNotFiredEarly` (12 h, asserts it does **not** fire at 11 h) |
| 10 | Zero delay | `ZeroDelayRunsBatchesBackToBack` |
| 11 | Printer unavailable | `AnUnavailablePrinterBlocksInsteadOfSubmitting`, `ABlockedJobResumesOnceThePrinterComesBack` |
| 12 | Printer disconnected | `APrinterRemovedFromWindowsStopsTheJobSafely`, `AnUnreachableDeviceDoesNotBlockAHealthyWindowsQueue` |
| 13 | Closed during the wait | `ClosingDuringTheRestPeriodResumesAtTheNextBatchNotTheFirst` |
| 14 | Closed while printing | `CrashingMidBatchNeverResendsThatBatchByItself`, `AJobStillInTheQueueAfterARestartIsAdoptedNotDuplicated` |
| 15 | Windows restarted | Every `RecoveryTests` case rebuilds the engine and store from disk |
| 16 | Print job failure | `AFailureBeforeAnyOutputIsRetriedAutomatically`, `AFailureAfterPartialOutputIsNeverRetriedAutomatically` |
| 17 | Duplicate / recovery | `NoVerdictWithAnyPhysicalOutputIsEverSafelyRetryable` and all of `RecoveryTests` |
| 18 | Invalid / corrupt PDF | Verified against real fixtures (below) |
| 19 | Multiple printers | Verified against the real spooler (below) |
| 20 | Printer name changing | `APrinterRemovedFromWindowsStopsTheJobSafely` |

---

## The central safety property

One test sweeps the entire decision space of `BatchVerifier` — every combination
of counter delta, spool outcome, queue observation, page count and printed flag
— and asserts a single invariant:

> An automatic resend is permitted **only** where the evidence positively proves
> nothing printed.

```csharp
if (!verdict.SafeToAutoRetry) continue;

var provesNothingPrinted =
    delta == 0 ||
    (beforeOutput && !inQueue) ||
    (!spooled && !inQueue && delta is null);

provesNothingPrinted.Should().BeTrue(
    "an automatic resend was allowed without proof that nothing printed");
```

Supported by:

- `WindowsSayingPrintedWhileTheCounterSaysNothingIsAmbiguous` — the two sources
  contradict each other, so the application refuses to choose.
- `PartialPhysicalCountIsAmbiguousAndReportsHowFarItGot` — 18 of 50 sheets is
  never silently resent, and the number is surfaced.
- `MorePagesThanExpectedCannotBeAttributed` — somebody else printed too.
- `TheSubmitIntentIsOnDiskBeforeAnythingReachesTheSpooler` — opens a **separate**
  `JobStore` mid-submission and asserts the intent is already readable.
- `EveryAttemptGetsAUniqueDocumentTokenSoRecoveryCannotConfuseThem`.

---

## Hardware verification

Automated tests cannot prove the Win32 interop or the rendering pipeline are
right. Those were verified directly against the real printer — an **HP Ink Tank
Wireless 410/419 (Z6Z97A)** on a WSD port.

### Spooler interop

Enumerated all six installed printers; read status, attributes, port, driver and
queue depth for each; confirmed a non-existent printer returns
`querySucceeded = false` rather than an empty-but-successful result.

**Finding that shaped the design:** the HP reports `Status = 0x00000000` —
"Ready" — essentially always, including when asleep. Windows alone will not tell
you this printer is in trouble, which is the entire justification for the device
probe.

### Page ranges actually printed

Pages 51–100 of a 200-page fixture were printed as one real spooler job to
Microsoft Print to PDF, then the output was matched back to the source:

```
source pages : 200
printed pages: 50
identified   : 51..100 (50 pages, contiguous ascending)
separation   : 2.4x (runner-up vs best)
RESULT: PASS - exact page range, correct order, no duplicates, no gaps
```

Because the output is rasterised and has no text layer, `verify` reduces every
page of both files to a grayscale fingerprint and matches each printed page to
its nearest source page. The separation figure confirms the match is decisive
rather than a coincidence.

### PDFium pixel format

`rendercheck` scanned a rendered page and found **34,816 pixels whose colour
channels exceed their alpha** — only possible under a straight (non-premultiplied)
alpha model, on a fully transparent background.

This mattered: an unpainted pixel is `(0,0,0,0)`, so compositing with
`SourceCopy` would have produced **solid black pages**. Flattening onto white
explicitly fixed it and, as a side effect, cut spooling from **4.8 s to 1.6 s per
page**.

### End-to-end on the real printer

A 3-page document, 1 page per batch, 1 minute apart, through the full
application:

```
13:46:06  Batch 1 of 3 submitted (Page 1)      keep-awake acquired
13:46:45  Batch 1 completed
13:46:45  Resting the printer for 1 minutes
13:47:45  Batch 2 of 3 submitted (Page 2)
13:48:22  Batch 2 completed
13:49:22  Batch 3 of 3 submitted (Page 3)
13:49:57  Batch 3 completed — all 3 pages printed
13:49:57  keep-awake released
```

Three separate spooler jobs, each with its own document token, correct rests,
three sheets of paper, and the printer's impression counter advancing in step.

### Recovery on real hardware

The application was killed mid-job and restarted. Recovery found the unfinished
job, reported that a batch had been interrupted, and left it for the user with
both decision buttons — **without submitting anything**.

### Device probe

Against the real printer, confirming both channels work and produce the counters
verification depends on:

```
resolved: 192.168.29.167   [64 ms]
source=IPP + HP LEDM
  state=Idle (Ready)          accepting=True   queued=0
  model/serial     : Ink Tank Wireless 410 series / CN98L5G2MH06V1
  printAppImpress. : 23338        <- the verification counter
  engineImpress.   : 24075
  jams / mispicks  : 64 / 1486
```

Address resolution via the registry `Location` value takes **64 ms** and is
deterministic. WS-Discovery, the fallback, proved unreliable on this hardware:
it failed outright on one attempt and returned two ambiguous candidates on
another. Both are now handled.

Ports that cannot lead to a network device skip discovery entirely — **89 ms**
instead of **5,169 ms** on a virtual printer.

### PDF error handling

Against real malformed fixtures:

| File | Result |
|---|---|
| `pages-200.pdf` | 200 pages, A4, PDF 1.4, title and author read |
| `not-a-pdf.pdf` | *"This file does not start with a PDF header, so it is not a PDF."* |
| `truncated.pdf` | *"This file could not be opened as a PDF. It may be corrupted…"* |
| `empty.pdf` | *"The file is empty (0 bytes)."* |

All rejected at load, before a job is created — not halfway through batch 3.

### Installer

Installed, verified, uninstalled and reinstalled:

- 266 files (156.7 MB) to `%LOCALAPPDATA%\Programs\PrintPacer`
- Start Menu and Desktop shortcuts with correct target and icon
- Registered in **Settings → Apps → Installed apps** with a working uninstall
  command
- The installed copy launches and runs
- Uninstall removes the program, both shortcuts and the registry entry, and
  **keeps** `%LOCALAPPDATA%\PrintPacer` so logs survive

---

## Bugs this testing found

Worth recording, because each was a genuine defect rather than a test problem.

| Bug | Found by | Consequence if shipped |
|---|---|---|
| `SourceCopy` on straight-alpha BGRA | `rendercheck` | **Every page solid black** |
| UI never left the setup screen | Running the app | Dashboard unreachable; root cause was the UI reading a job the engine was mutating |
| Job could start batch 2 with batch 1 unresolved | `RecoveryTests` | Pages out of order, and a pending question buried |
| A user decision could be overwritten by the monitor | `APartiallyPrintedBatchCanBeResumedWithout…` | A human's "these printed" silently reverted |
| No rest after a recovered batch | Flaky test | Next batch fired immediately at the moment the printer is hottest — defeating the entire purpose |
| `PdfDocumentOpenMode.InformationOnly` | Build warning | Metadata silently always empty |
| Two build output trees | Stale binary | Testing a binary that is not the one being changed |

---

## Running a real overnight test

Before trusting it with a 500-page document, do one full rehearsal.

### 1. Rehearse the mechanism in ten minutes

```powershell
$probe = ".\tools\PrintPacer.Probe\bin\Debug\net8.0-windows\PrintPacer.Probe.exe"
& $probe makepdf rehearsal.pdf 12 "Overnight rehearsal"
```

Open it in PrintPacer, set **3 pages per batch** and **2 minutes** apart, and
start. In about ten minutes you should get four separate jobs, four rests, and
twelve sheets.

While it runs, confirm:

- Four distinct entries appear in the Windows print queue, not one
- The batch list ticks over to **Printed**
- The countdown between batches is honest
- Closing the window keeps it running in the notification area

### 2. Rehearse the recovery

Halfway through, kill it:

```powershell
Stop-Process -Name PrintPacer -Force
```

Reopen it. You should see the recovery summary. Confirm the already-printed
batches are still marked printed and **nothing is resent**. This is the
behaviour you are actually relying on overnight.

### 3. Then the real thing

```
Pages per batch   50        (30 if it still stalls)
Rest between      60 minutes
Start             immediately, or a time before you go to bed
```

- Load enough paper for the whole document, plus a margin
- Check the ink
- Leave **Keep the computer awake** on
- Close the window, do not exit
- Leave the printer powered on

Work out the finish time first: batches × rest. 500 pages at 50 per batch with
60-minute rests is ten batches — about **nine hours** including print time.

### 4. In the morning

1. Count the paper against the **pages** figure on screen.
2. If a batch says **Needs checking**, read the reason and use the buttons.
   Prefer *"Keep the N that printed, send the rest"* — it is there to save paper.
3. **Export log…** if anything looks odd; it records every batch, every attempt,
   the printer's counter before and after, and the reasoning behind each verdict.

### What to expect if the printer misbehaves

| What happens | What PrintPacer does |
|---|---|
| Runs out of paper | Stops, notifies, waits, resumes when refilled |
| Jams mid-batch | Reports how many sheets came out; resends only the rest |
| Goes to sleep | Nothing — it wakes when the next batch arrives |
| Switched off | Blocks, re-checks every 5 minutes, escalates after 4 hours |
| Stalls silently | Detected after 45 minutes of no progress; **never** resends on its own |
