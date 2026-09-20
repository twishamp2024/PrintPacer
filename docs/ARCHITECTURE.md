# Architecture

How PrintPacer works, and why it is built the way it is.

---

## 1. The problem, precisely

An HP Ink Tank inkjet stalls partway through a long continuous print job. The
fix its owner already uses by hand is to print in chunks and let the printer
rest in between. The software job is to do that reliably while nobody is
watching.

Two requirements dominate every design decision:

1. **Each batch must be its own Windows print job.** Handing the spooler one
   200-page job and merely pausing between page ranges would change nothing —
   the printer still sees one continuous job. The pacing only works if the
   printer genuinely finishes, goes idle, and starts again later.

2. **Nothing may print twice.** The application runs unattended for hours.
   Any moment of confusion must resolve towards "ask the human", never towards
   "send it again and hope".

Everything below follows from those two.

---

## 2. Technology

**C# 12 on .NET 8, WPF for the interface.**

The application is a thin layer over three Windows subsystems — the print
spooler, power management, and the notification area — and a PDF renderer. That
makes the choice mostly about which platform reaches those APIs without
friction:

| Considered | Why not |
|---|---|
| Electron | Would need a native shim for every one of `EnumJobs`, `GetPrinter`, `PowerSetRequest` and `NotifyIcon`, plus ~150 MB of runtime, to end up with less direct access than the alternatives. |
| Python + Qt | Workable, but shipping a dependency-free installer means bundling an interpreter and a PDF stack, and `ctypes` structure marshalling for `JOB_INFO_2` is noticeably more error-prone than a declared struct. |
| WinUI 3 | The right long-term answer for a new Store app, but heavier deployment for a single-window desktop utility, with no benefit here. |
| **WPF on .NET 8** | **Chosen.** P/Invoke to winspool.drv is first-class, `System.Drawing.Printing` gives direct access to the printer device context, self-contained publish produces a folder that runs on a machine with no .NET at all, and the UI toolkit is stable and well understood. |

x64 is pinned throughout, because both native dependencies (PDFium and SQLite)
are loaded in-process.

---

## 3. Project layout

```
PrintPacer.Core          No UI. Everything that must be correct.
├── Model/               Job, Batch, BatchAttempt, settings, state enums
├── Spooler/             Win32 print-spooler interop, behind ISpooler
├── Device/              IPP and HP LEDM clients, address discovery
├── Pdf/                 Document inspection
├── Printing/            IPrintEngine and the PDFium implementation
├── Persistence/         SQLite job store, settings store, paths
├── Engine/              The scheduler, the verifier, the clock
├── Simulation/          A software printer, for testing
└── Diagnostics/         Logging setup

PrintPacer.App           WPF. Projection and input only; no decisions.
├── ViewModels/          Screen state derived from engine snapshots
├── Views/               XAML
├── Platform/            Power requests, tray icon, binding diagnostics
└── Themes/              Styles

PrintPacer.Probe         Development tool. Not shipped.
```

The split matters: `Core` has no reference to WPF and no knowledge that a user
interface exists. Every rule about what may be reprinted lives there, and is
tested there.

---

## 4. How a batch gets printed

### 4.1 One batch, one spooler job

`PdfiumPrintEngine` renders a page range and draws it onto the printer's device
context. One call to `PrintDocument.Print()` produces exactly one spooler job.

The source PDF is **never rewritten**. There is no intermediate split file to
get out of sync, corrupt, or leave behind in a temporary directory — the engine
reads page *n* of the original document and draws it. This also means the page
numbering can never drift: batch 3 draws pages 101–150 of the file on disk, not
of a copy made earlier.

### 4.2 Rendering

Pages are rasterised by **PDFium** — the engine inside Chrome and Edge — at a
configurable resolution, 300 dpi by default, then drawn to the printer DC.

A note on fidelity: the HP Ink Tank is a PCL-3 raster device with no PostScript
or PDF interpreter, so its driver rasterises whatever it is given regardless.
Rendering through GDI costs no fidelity the driver would not have removed anyway.

Two details here were established by measurement, not assumption:

**PDFium returns straight (non-premultiplied) BGRA on a fully transparent
background.** This was verified by scanning a rendered page for pixels whose
colour channels exceed their alpha — 34,816 of them, which is only possible
under a straight-alpha model. The consequence is significant: an unpainted pixel
is `(0,0,0,0)`, so compositing with `SourceCopy` would emit **solid black pages**.
The engine therefore flattens the buffer onto white itself before handing GDI+ an
opaque bitmap. Doing that explicitly also removes any dependence on how a
particular printer driver handles alpha on its device context.

**Flattening is faster than alpha-blending.** Most pixels on a text page are
fully transparent or fully opaque, so both are handled with a single 32-bit
store and only antialiased edges need arithmetic. Combined with dropping
high-quality bicubic resampling — which buys nothing visible on an inkjet —
this took spooling from **4.8 s to 1.6 s per page**.

### 4.3 Identifying our own jobs

Every submission gets a unique spooler document name:

```
textbook [51-100] PrintPacer#a1b2c3d4.2.1.9f8e7d6c5b4a
└ file    └ range  └ marker └ job └ batch └ attempt └ unique
```

Readable in the Windows print queue, and unambiguous to the application. This
token is how a restarted process recognises a job it submitted before it died,
and it is never reused between attempts.

---

## 5. Not printing twice

This is the heart of the design.

### 5.1 Write-ahead intent

```
1.  Read the printer's physical page counter          ← before anything
2.  Write the intent to SQLite and flush to disk      ← returns only when durable
3.  Call the print API
4.  Discover and record the spooler job id
5.  Monitor until the job leaves the queue
6.  Read the counter again
7.  Decide, and record the evidence
```

Step 2 is load-bearing. `JobStore.RecordSubmitIntent` runs in WAL mode with
`synchronous=FULL`, so when it returns, the record has reached the platter. A
crash one instruction later still leaves a record saying *a job may exist in the
Windows queue for batch 2 with this exact document name*.

There is therefore **no window** in which a print job can exist that the
application has no record of. That single property is what makes safe recovery
possible at all.

### 5.2 Two independent witnesses

| Channel | Tells us | Weakness |
|---|---|---|
| Windows spooler (`EnumJobs`, `GetPrinter`) | Job exists, its status, pages reported, when it left the queue | On a consumer inkjet, "printed" means *the data reached the device*, not *paper came out* |
| The printer itself (IPP + HP LEDM) | Real device state, and a lifetime physical page counter | May be asleep, unreachable, or absent entirely |

The physical counter is the closest thing to ground truth available without a
human looking at the output tray. Its delta across a batch is checked against
the expected page count.

Because the counter is updated asynchronously by the device, reading it the
instant the queue empties would systematically under-count and manufacture false
alarms. The engine polls it until it either reaches the expected total or stops
changing for two consecutive reads, up to a 90-second settle window.

### 5.3 The verdict table

`BatchVerifier` is a pure function — no I/O, no state — which makes it
exhaustively testable.

| Counter delta | Spooler says | Verdict | Auto-resend? |
|---|---|---|---|
| exactly N | anything | **Verified** | — |
| 0 | did not confirm | **Failed** | **Yes** — proven nothing printed |
| 0 | "printed, N pages" | **Ambiguous** | No — the two sources contradict each other |
| 0 < d < N | anything | **Ambiguous**, d sheets counted | No — offers resume from sheet d+1 |
| > N | anything | **Ambiguous** | No — something else printed too |
| unavailable | PRINTED flag + full page count | **Verified** (lower confidence) | — |
| unavailable | short count, or never seen | **Ambiguous** | No |
| nothing was handed over at all | submission failed before output | **Failed** | **Yes** |

The invariant, asserted by a test that sweeps the entire decision space:

> An automatic resend is permitted **only** where the evidence positively proves
> nothing printed.

Note the third row. When Windows claims success and the hardware counter has not
moved, they cannot both be right, so the application refuses to choose. That
single row is the difference between a tool you can trust overnight and one you
cannot.

### 5.4 Recovery

On startup, every attempt left in flight is resolved:

- **Still in the Windows queue** → adopt it and keep monitoring. Never submit a
  second copy.
- **Gone, counter advanced by exactly N** → it printed.
- **Gone, counter never moved** → it did not print; safe to resend.
- **Gone, counter moved partially** → ask, offering resume from the exact sheet.
- **No counter available** → ask.

A batch adopted on one restart and finished while the application was closed is
settled on the *next* restart from the counter, without troubling the user. That
re-evaluation only ever moves a batch towards "completed" — it can never trigger
a resend.

One subtlety worth stating: when recovery concludes a batch printed, the next
batch does **not** start immediately. The application cannot know when the
interrupted batch actually finished, so it assumes "just now" and applies the
full rest period. That is the moment the printer is most likely to be hot, and
starting straight away would defeat the point of the application.

---

## 6. Time

Delays are stored as **absolute UTC instants**, never countdowns. A countdown in
memory does not survive a restart; an absolute deadline does.

Two conditions can make a deadline due, and the pair covers every way a clock
can misbehave:

- **Wall-clock time reaches it.** This is the normal path, and it correctly
  credits time spent asleep or hibernating — an hour of hibernation *is* an hour
  of the printer cooling down.
- **The monotonic clock has run for the full planned duration.** A backstop, so
  that a backwards clock correction cannot strand a job forever. Backwards jumps
  are detected and the deadline re-anchored.

`Deadline` takes both `ISystemClock.UtcNow` and `Monotonic` (`GetTickCount64`,
which by design excludes suspended time). Injecting the clock is what lets the
tests run a twelve-hour schedule in milliseconds.

---

## 7. Concurrency

One thread makes every decision.

`JobEngine` runs a single loop fed by a command channel. UI actions post
commands; the loop applies them between ticks. There are no locks around job
state and no possibility of two code paths mutating a batch at once.

The interface receives **immutable snapshots** (`JobSnapshot`, `BatchSnapshot`)
built on the engine loop. Handing the UI a live mutable `PrintJob` would mean the
window enumerating a batch list while the engine appends to it — a race that
shows up as an occasional blank screen or an unreproducible crash in the middle
of an overnight run. (It did, during development, and this is what fixed it.)

`JobEngine.Jobs` is `internal` so this cannot be circumvented by accident;
callers outside the engine use `GetJobsAsync()` or the snapshot from
`JobChanged`.

One consequence deserves stating: because commands and monitoring share the
loop, a user decision about a batch cannot interleave with the monitor reaching
its own verdict about that same batch. Resolving a batch by hand first abandons
any active run for it, so a decision made by looking at real paper is never
overwritten a moment later by the software's own guess.

---

## 8. Printer monitoring

### 8.1 What Windows exposes

Via `GetPrinter` level 2 and `EnumJobs` level 2: printer status bits (offline,
paused, paper jam, paper out, door open, needs intervention, …), the
"Use Printer Offline" attribute, queued job count, and per-job status, total
pages and pages printed.

Measured on the target hardware: the HP Ink Tank over a WSD port reports
`Status = 0x00000000` — "Ready" — essentially always, including when it is
asleep. **Windows alone will not tell you this printer is in trouble.** That
finding is the entire justification for the device probe.

A distinction enforced throughout the spooler layer: a *failed query* is
reported as a failed query, never as an empty or healthy result. `ListJobs`
returns `querySucceeded` separately from the job list, because "I could not ask"
and "I asked and the job is gone" must never be conflated — conflating them is
precisely how duplicate printing happens.

### 8.2 What the printer exposes

- **IPP** (RFC 8011, standard across essentially every network printer of the
  last fifteen years): `printer-state`, `printer-state-reasons`,
  `printer-is-accepting-jobs`, `queued-job-count`.
- **HP LEDM** (`/DevMgmt/*.xml`, HP-specific): status categories and alerts, and
  crucially the physical counters — impressions, jam events, mispick events.

Both clients are small and dependency-free: a ~200-line IPP encoder/parser and
namespace-agnostic XML reads. Pulling in a full IPP stack for four attributes
would be a poor trade.

The device probe may only ever **add** a reason to hold back, never remove one.
A sleeping network printer stops answering, and that must not be mistaken for a
fault, or the job would never start.

### 8.3 Finding the printer's address

A WSD port name is a GUID and contains no address. Resolution, in order:

1. An address in the port name (standard TCP/IP ports).
2. The Standard TCP/IP Port monitor's registry entry.
3. **The printer's own `Location` registry value.** For a WSD device Windows
   records something like `http://[fe80::9e7b:efff:feda:95ed%13]:3911/`. The
   hardware address is recoverable from that link-local address by reversing the
   modified EUI-64 rule, and Windows' neighbour cache maps it to the IPv4 address
   the printer is using right now. (The printers serve their management
   interface on IPv4 only, so the IPv6 address is not usable directly.)
4. WS-Discovery multicast, as a last resort.

Step 3 is deterministic and takes **~65 ms**. WS-Discovery is multicast, lossy,
and a printer in power save may miss the probe entirely — on this hardware it
failed outright on one attempt and returned two ambiguous candidates on another.
When several devices do answer, the known hardware address disambiguates them.

Ports that cannot possibly lead to a network device (`nul:`, `PORTPROMPT:`,
`LPT*`, Store app ports, …) skip discovery entirely, turning a 5-second
multicast sweep into an 89 ms no-op on every virtual printer.

---

## 9. Persistence

SQLite, WAL mode, `synchronous=FULL`, one connection serialised behind a lock.
Write volume is a handful of rows per batch, so there is nothing to gain from
concurrency and a great deal to lose from a subtle interleaving bug.

```
jobs      configuration and current state
batches   page ranges and their outcomes
attempts  one row per submission: document token, spooler job id,
          counters before and after, evidence flags, verdict text
events    the human-readable timeline
```

The `attempts` table is the audit trail. It records not just *what* was decided
but *what was observed*, so the interface can explain its reasoning rather than
merely assert a verdict.

Settings live in a separate JSON file, deliberately: they are hand-edited often
enough that a malformed file is realistic, and a malformed settings file must
never be able to stop an in-progress job from being recovered. A bad file is
backed up and replaced with defaults.

---

## 10. Overnight operation

**Power.** `PowerCreateRequest` / `PowerSetRequest` with
`PowerRequestSystemRequired` only — deliberately *not* `DisplayRequired`, so the
screen may sleep and the lid may close while the job runs. The request carries a
readable reason, so it appears in `powercfg /requests` as PrintPacer rather than
as an anonymous process holding the machine awake. Released as soon as there is
nothing left to do.

**Window lifetime.** Closing the window hides to the notification area rather
than exiting; the most likely way to ruin an overnight print is a reflexive
click on the X before bed. Exiting from the tray menu warns if a job is running.

**Single instance.** A named mutex. Two copies driving the same queue would each
believe they owned the in-flight batch.

**Sleep and shutdown.** `SystemEvents.PowerModeChanged` and `SessionEnding`
checkpoint the database; the absolute deadlines then do the rest on wake.

**Notifications** go through the shell's notification-area API, which Windows 10
and 11 render as ordinary toasts — so they land in the Action Centre and honour
Focus Assist without needing a packaged identity or a registered AppUserModelID.
That avoids a class of bug where the installer and the running application
disagree about the app id and notifications silently vanish. They are also
deliberately sparse: a blocked printer notifies at most every 30 minutes.

---

## 11. Testing strategy

70 automated tests, ~24 seconds, no hardware required.

The engine runs its **real** loop against its **real** SQLite store; only the
world outside the application is simulated. A pass means the shipped code paths
work, not that a parallel implementation does.

`SimulatedPrinter` models a printer and its queue, with switches for the
failures that are impractical to stage with real hardware: stalling mid-batch,
a queue that cannot be read, a page counter that lags the spooler or freezes
entirely, a printer that vanishes from Windows, and jobs that disappear without
printing.

`ManualClock` makes twelve-hour delays instant, and distinguishes normal elapsed
time from suspend (wall clock advances, monotonic does not) and from a clock
that jumps.

Details and results: [TESTING.md](TESTING.md).

---

## 12. Dependencies

| Package | Why this one |
|---|---|
| **Docnet.Core** (PDFium) | PDFium is the most widely exercised PDF renderer in existence. Page count comes from the same library that does the rendering, so the count used for batch boundaries can never disagree with what is drawn. |
| **PDFsharp** | Reads the document information dictionary, which PDFium does not expose. Metadata only, and failure is non-fatal. |
| **Microsoft.Data.Sqlite** | Durability with real fsync semantics, in-process, no server, no schema migration framework needed. |
| **Serilog** | Structured logging to a rolling file with retention. |
| **xUnit**, **FluentAssertions** | Tests only; not shipped. |

Deliberately *not* taken:

- **No external PDF printing utility.** SumatraPDF or similar would work, but the
  application could then only observe an exit code — far weaker than driving the
  print loop directly and knowing exactly which page failed. Detecting whether
  the print actually succeeded is the whole point.
- **No MVVM framework.** One window; `ObservableObject` and `RelayCommand` are
  about forty lines between them.
- **No dependency injection container.** The composition root is one method.
- **No cloud anything.**
