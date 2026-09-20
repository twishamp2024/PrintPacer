# Critical self-review

A deliberate pass over each failure mode the brief called out, with what was
found and what was done about it. Items marked **FIXED** were defects found
during this review; the rest record how the risk is handled and why that is
believed to be sufficient.

---

## Duplicate printing

The primary risk. Defended in four layers:

1. **Write-ahead intent.** `RecordSubmitIntent` commits to SQLite with
   `synchronous=FULL` *before* the print API is called, so no job can exist in
   the Windows queue that the application has no record of.
2. **Unique document token per attempt**, never reused, so a restarted process
   can recognise its own job and re-attach rather than submit a second.
3. **Two independent witnesses** — the spooler and the printer's physical page
   counter — with disagreement resolving to "ask", never to "resend".
4. **A test that sweeps the whole decision space** asserting that an automatic
   resend is permitted only where the evidence proves nothing printed.

**Residual risk.** If the printer has no reachable counter *and* the spooler
gives an ambiguous answer, the outcome genuinely cannot be determined. The
application stops and asks rather than guessing. This is surfaced in the
interface, not hidden.

---

## Lost print jobs

Every path from "decided to print" to "verified" is persisted, so a crash at any
point leaves a recoverable record.

**FIXED — cancelling before the job id was known.** The spooler job id is
discovered a moment *after* submission. A cancel issued in that window found
nothing to cancel, so the batch printed anyway — after the user had been told it
was stopped. `CancelSpoolerJobAsync` now falls back to locating the job by its
document token.

---

## Race conditions

**FIXED — the user interface read job state the engine was mutating.** The
engine passed live `PrintJob` objects to the UI thread, which then enumerated
`job.Batches` while the engine appended to it. The visible symptom was the
dashboard never appearing at all; the invisible one would have been intermittent
crashes overnight.

The engine now publishes **immutable snapshots** built on its own loop, and
`JobEngine.Jobs` is `internal` so this cannot be reintroduced by accident.

**FIXED — a user decision could be overwritten by the monitor.** Resolving a
batch by hand while that batch was still being monitored let the monitor reach
its own verdict moments later and overwrite the human's answer.
`ReleaseBatchForManualDecisionAsync` now abandons the active run first. A
judgement made by looking at real paper always wins.

Structurally: one engine loop makes every decision, fed by a command channel.
There are no locks around job state because nothing else touches it.

---

## Timer reliability

Deadlines are **absolute UTC instants**, never countdowns, so they survive a
restart. Two conditions can fire one:

- Wall-clock time reaches it — which correctly credits time spent asleep.
- The monotonic clock has run the full planned duration — a backstop against a
  backwards clock correction stranding a job.

Covered by tests for a 12-hour delay (asserting it does *not* fire at 11 hours),
for suspend, and for a clock wound back a year.

---

## Crash recovery

Every interrupted attempt is resolved on startup: adopted if still queued,
settled from the page counter if not, and referred to the user when neither is
conclusive. A batch adopted on one restart and finished while the application
was closed is settled on the next restart without troubling anyone.

**FIXED — no rest after a recovered batch.** When recovery concluded a batch had
printed, the next batch started *immediately*. The application cannot know when
the interrupted batch actually finished, so this fired at the exact moment the
printer was most likely to be hot — defeating the entire purpose of the tool.
Recovery now applies the full rest period from the moment of recovery.

Found by a flaky test, which is worth noting: the flakiness was the symptom, the
missing rest was the disease.

---

## Printer disconnection

A failed query is reported as a failed query. `ISpooler.ListJobs` returns
`querySucceeded` separately from the job list, because "I could not ask" and "I
asked and the job is gone" must never be conflated — conflating them is exactly
how duplicate printing happens.

A disconnected printer blocks submission, is re-checked on an interval, and
escalates to the user after a configurable period rather than retrying silently
until morning. The device probe may only ever *add* a reason to hold back: a
sleeping network printer that stops answering must not be mistaken for a fault.

---

## Windows sleep and restart

Power is held with `PowerSetRequest(PowerRequestSystemRequired)` only — not
`DisplayRequired` — so the screen sleeps and the lid closes while the job runs.
Released as soon as there is nothing to do. `SessionEnding` and `PowerModeChanged`
checkpoint the database; absolute deadlines do the rest on wake.

---

## Temporary-file cleanup

**FIXED — dead settings describing files that never existed.**
`TempDirectoryOverride` and `DeleteTempFilesAfterBatch` were declared and
persisted but never read, and a `temp` directory was created and never written
to. They described an architecture the application does not use.

The print engine reads page ranges straight out of the source PDF and draws
them; it never writes an intermediate file. That removes temporary-file cleanup
as a category of bug rather than making it configurable, so the settings and the
directory were removed instead of being wired up.

The source PDF is never modified, moved or copied.

---

## Incorrect page ranges

**FIXED — a resumed remainder printed out of order.** When a partly printed
batch is split, the remainder is added with a new, higher batch number. Batch
selection ordered by *number*, so for a 200-page job where batch 1 stopped at
page 20, the output order would have been:

```
pages 1-20, 51-100, 101-150, 151-200, 21-50      ← wrong
```

The remaining pages would have come out last, at the bottom of the stack.
Selection and display now order by **first page**, which is what the reader
actually cares about:

```
pages 1-20, 21-50, 51-100, 101-150, 151-200      ← correct
```

Covered by a new test that asserts the order in which ranges reach the printer,
end to end, for a job with a resumed remainder.

Otherwise: batch planning is property-tested across many page counts and batch
sizes for "every page exactly once, in order", and the source file is
SHA-256-checked before every batch so an edited document stops the job rather
than printing the wrong pages.

---

## UI state inconsistencies

The interface owns no job state. Everything shown is projected from the engine's
persisted snapshot, so the screen and the database cannot disagree.

Data-binding failures — otherwise silent, rendering a control blank in a way
indistinguishable from a field the user cleared — are routed into the
application log by `BindingErrorListener`.

Also fixed during development: a `TextBox` template that drew its border and
nothing else, so the batch-size and delay fields appeared empty; and a printer
list defaulting to the alphabetically-first queue, which was the fax driver.

---

## Unsafe automatic retries

Only a verdict of `Failed` — meaning the evidence positively shows nothing
printed — permits an automatic resend. Retries are capped, spaced by a retry
interval, and escalate to the user. Every manual resend asks for confirmation
first and says plainly that pages which did print will print again.

A stalled batch is **never** resent automatically. That is the failure this
application exists to manage, and answering it by sending more paper would be
the worst possible response.

---

## Other findings

| Finding | Consequence if shipped |
|---|---|
| `SourceCopy` on straight-alpha BGRA | **Every page solid black.** PDFium returns a transparent background; fixed by flattening onto white, which also cut spooling from 4.8 s to 1.6 s per page. |
| Job could start batch 2 with batch 1 unresolved | Pages out of order and a pending question buried. Unresolved batches now block everything after them. |
| `PdfDocumentOpenMode.InformationOnly` | Document metadata silently always empty — the mode is unimplemented in PDFsharp 6. |
| Two build output trees | Solution and project builds emitted to different folders; I had been testing a stale binary. |
| `SpoolerPollSeconds` declared but unused | A setting that did nothing. Now honoured, with zero meaning "every tick". |
| WS-Discovery unreliable | Failed outright once and returned two ambiguous candidates another time. Replaced as the primary path by a deterministic registry lookup (~65 ms), with hardware-address disambiguation as backup. |
| Discovery attempted on virtual printers | A 5-second multicast sweep on every readiness check for a `nul:` printer. Now 89 ms. |

---

## What remains genuinely unsolved

Stated plainly rather than papered over:

- **No counter, ambiguous spooler answer** → the user must look at the paper.
- **Shared printer** → a counter delta that includes somebody else's pages
  cannot be attributed, so that batch is referred to the user.
- **Physical printer restart** → impossible in software; the application does not
  pretend otherwise, and offers no button that merely restarts the spooler.
- **Out of paper at 3 a.m.** → it stops, notifies and waits. It cannot load paper.

The full list is in [LIMITATIONS.md](LIMITATIONS.md).
