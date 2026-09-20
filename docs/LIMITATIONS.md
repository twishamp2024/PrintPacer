# Known limitations

What PrintPacer cannot do, what it refuses to pretend, and how it behaves at
each boundary.

Every item here is a deliberate decision, not an oversight. Where a capability
is genuinely unavailable, the application says so rather than faking it.

---

## 1. It cannot restart your printer

**There is no software way to power-cycle an HP Ink Tank.** No API, no IPP
operation, no LEDM endpoint. The printer's own management interface exposes
status and counters, not a power switch.

Four separate things are often confused, so to be explicit:

| Action | Possible? | In PrintPacer |
|---|---|---|
| Restart the Windows Print Spooler service | Yes, with administrator rights | **No.** Out of scope, needs elevation, and rarely the real problem |
| Clear or cancel a stuck Windows print job | Yes | **Yes** — "Cancel queued batch" |
| Reconnect to the printer | Nothing to reconnect; the spooler handles it | **Yes**, implicitly — it re-checks and resumes |
| Physically power-cycle the printer | **No** | **No**, and it does not offer a button that pretends to |

A "Restart printer" button that merely restarted the spooler would be a lie
about what had happened to the hardware. When a batch stalls, PrintPacer reports
what it knows — including how many sheets physically emerged — and waits for you.

---

## 2. "Printed" from Windows does not mean paper

The Windows spooler reports a job as printed once the data has been handed to
the device. On a consumer inkjet that can be well before — or instead of — ink
reaching paper.

This is why the printer's own physical page counter matters, and why PrintPacer
treats a disagreement between the two as *unresolved* rather than picking a
winner. If your printer has no reachable counter, spooler-only verification is
used and interrupted batches are referred to you more often. That is the honest
trade, and it is stated on screen.

---

## 3. What the page counter can and cannot prove

The counter is a lifetime total, so its delta across a batch is only
attributable to that batch if nothing else printed at the same time.

- If somebody prints from a phone mid-batch, the delta overshoots. PrintPacer
  detects this and reports it as unattributable rather than guessing.
- It counts **sheets marked**, not sheets that came out correctly. A page that
  jams after the mark is counted.
- It updates asynchronously. PrintPacer waits for it to settle (up to 90 s)
  before judging, because reading it too early would manufacture false alarms.

---

## 4. Verification is weaker without a network printer

USB-only printers, printers with the management interface disabled, and
printers on a network the PC cannot reach all fall back to spooler-only
evidence.

Everything still works. The difference is only in the ambiguous cases: with a
counter, most interrupted batches resolve themselves; without one, you are asked
to look at the paper.

---

## 5. Pause cannot recall a batch already sent

Once pages are in the printer's queue, Windows offers no reliable way to un-send
them. Pause stops the *next* batch. The interface says exactly this rather than
implying more.

**Cancel queued batch** removes the job from the Windows queue, but pages
already transferred to the printer's own memory may still print. Afterwards,
PrintPacer asks what actually came out.

---

## 6. Rendering, not passthrough

Pages are rasterised with PDFium and drawn to the printer's device context.
The source PDF is never modified.

For a PCL-3 device like the HP Ink Tank this costs nothing — the driver
rasterises regardless. On a PostScript printer, vector passthrough would give
sharper text at the same file size; PrintPacer does not implement it.

Practical consequences:

- Render resolution is a real trade-off. 300 dpi is the default; 600 dpi is
  sharper but roughly four times the memory (~140 MB per A4 page) and much
  slower.
- Spooling costs about **1.6 seconds per page** at 300 dpi. A 50-page batch takes
  around 80 seconds to spool — comfortably less than the printer takes to print
  it, so it is not the bottleneck, but it is not instant either.
- Very complex pages take longer and use more memory.

---

## 7. PDF support

Handled: standard PDFs, mixed page sizes, mixed orientations (auto-rotated to
fit), and PDFs without a valid metadata dictionary.

Not handled:

- **Password-protected PDFs.** Rejected at load with a clear message. There is no
  password prompt.
- **Corrupt or truncated files.** Rejected at load rather than failing halfway
  through batch 3.
- **Page selection.** The whole document is printed. There is no "print pages
  20–80 only".
- **Duplex, stapling, tray selection, N-up.** The printer's defaults are used.
  Set them in the Windows printer preferences beforehand.
- **Copies.** Fixed at one, deliberately — multiple copies would multiply any
  duplicate-printing mistake.

If the source file changes after a job is set up, PrintPacer detects it by
SHA-256 and stops, because page 51 of a different document is not page 51.

---

## 8. One job at a time

The data model, database schema and engine all support a job queue, and
`GetJobsAsync` already returns a list. The engine runs jobs one at a time in
order and will not start a second job while one is in flight — correct, since
they would share a printer.

What is missing is only the interface for it: the window shows a single job. A
second job can be created but the current build gives no way to manage a queue
of them. Adding that is a view-model change, not an architectural one.

---

## 9. Windows-only

Uses the Win32 print spooler, WPF, Windows power requests and the Windows
notification area. There is no macOS or Linux path, and no attempt at one.

Requires Windows 10 or 11, 64-bit. x64 only, because both native dependencies
(PDFium, SQLite) load in-process.

---

## 10. Things that will still defeat it

Honest list:

- **The printer runs out of paper mid-batch and you are asleep.** It stops,
  notifies, and waits. It cannot load paper.
- **The PC loses power.** State is recovered on next start, but nothing runs
  while the machine is off. Deadlines are absolute, so a batch due during the
  outage becomes due immediately on restart.
- **Somebody else prints to the same printer mid-batch.** The counter delta
  becomes unattributable and that batch gets referred to you.
- **The printer is removed from Windows mid-job.** Detected; the job stops and
  reports it.
- **The spooler service dies.** Queries fail, which PrintPacer treats as "cannot
  ask" — it waits rather than assuming the job finished. It does not restart the
  service.
- **A job is deleted from the Windows queue by someone else.** Looks identical to
  a job that vanished for any other reason, so it is reported as ambiguous.

---

## 11. Not implemented

Considered and deliberately left out of this version:

- Multi-job queue management in the interface (see §8)
- Printing a page range rather than the whole document
- Duplex and finishing options
- Restarting the Print Spooler service (needs elevation; rarely the real fix)
- Email or phone notification when a job finishes
- Scheduled recurring jobs
- Vector passthrough for PostScript printers
- Any form of telemetry, analytics or update check

---

## 12. Security and privacy boundaries

- **The application never needs administrator rights.** Reading print queues,
  submitting jobs and cancelling its own jobs are all available to a standard
  user, and its data lives in the user's own profile.
- **The setup.exe asks for elevation once**, purely because it writes to
  `C:\Program Files`. A user who cannot elevate is offered a per-user install,
  and the script-based installer in `installer\` needs no elevation at all.
- **Nothing leaves the machine** except requests to your own printer on your own
  network, and those are **read-only**: status and counters. The application
  never writes a setting to the printer.
- **PDFs are never copied.** They are read from where they are. No temporary
  copies of your document are created.
- **No telemetry**, no analytics, no update check, no cloud service.
- Data lives in `%LOCALAPPDATA%\PrintPacer` and is left there on uninstall so
  logs remain available. Remove it by hand, or uninstall with `-RemoveData`.
