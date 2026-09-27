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
- **Black & white depends on the driver.** PrintPacer asks for it the standard
  way, through the colour setting in the job's DEVMODE, and checks that the driver
  kept it. A driver that does not offer the setting (Microsoft Print to PDF is
  one) cannot be told to use black ink only; the pages are still rendered in
  greyscale, and the setup screen and the log say so. Options a driver keeps to
  itself, outside the standard setting, are not changed.

---

## 7. PDF support

Handled: standard PDFs, mixed page sizes, mixed orientations (auto-rotated to
fit), and PDFs without a valid metadata dictionary.

Not handled:

- **Password-protected PDFs.** Rejected at load with a clear message. There is no
  password prompt.
- **Corrupt or truncated files.** Rejected at load rather than failing halfway
  through batch 3.
- **Page ranges.** All pages, odd pages or even pages - but not "pages 20–80
  only".
- **Stapling, hole punching, tray selection, plain N-up.** The printer's defaults
  are used; set them in the Windows printer preferences beforehand. Double-sided
  printing, and paper and orientation for booklets and brochures, are set by
  PrintPacer (see §13).
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
- Printing an arbitrary page range (odd and even pages are supported)
- Finishing options (stapling, punching), paper tray selection
- Turning the paper over by hand under PrintPacer's control ("manual duplex") -
  a driver that does this itself still works, with someone present
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

---

## 13. Double-sided, booklets and brochures

**Double-sided is as reliable as the driver's word for it.** PrintPacer sets
double-sided in the job's DEVMODE, confirms the driver kept it before each batch,
and reads it back from the queued job in Windows. That proves Windows asked the
printer for both sides. It cannot prove the paper actually turned over - that is
between the driver and the hardware.

**Some "double-sided" printers need a person.** A printer without a duplex unit
may still offer double-sided printing through its driver, which prints the
fronts and then asks someone to turn the stack over. PrintPacer cannot tell this
apart from automatic duplex, so the setup screen warns about it whenever
double-sided is chosen. Such a batch will sit waiting overnight.

**Which way is "short edge"?** PrintPacer uses the standard meaning: short-edge
binding (`DMDUP_HORIZONTAL`) turns the physical sheet over on its short edge,
which is right for a landscape sheet folded down the middle. A driver that
interprets it relative to the page orientation will print the backs upside down;
switching **Flip on** to long edge corrects that. Do a one-sheet test on a new
printer before an overnight booklet.

**The page counter counts sides.** A double-sided batch of 10 sheets is
verified against 20 printed sides, because an impression is one side. A printer
whose counter counts sheets instead will show a mismatch, which PrintPacer
reports as *needs checking* - never as a reason to resend. A part-way count on a
double-sided batch offers no automatic resume point: 7 sides could be 7 fronts,
so the sheets themselves must be checked.

**Booklets.**
- No creep compensation. In a thick booklet the inner sheets stick out when
  folded, and trimming would eat into their outer margin. Fold thick booklets in
  sections (the **Folded in** option), or allow a larger margin.
- No crop or fold marks are printed.
- Left and right binding only; no top (calendar-style) binding.

**Brochures.**
- Bi-fold and letter-fold tri-fold only. Z-fold, gate fold and roll folds with
  more panels are not implemented.
- All panels are the same width. Commercial tri-folds often make the fold-in
  panel a few millimetres narrower; with equal panels, fold carefully or use a
  slightly larger space at the folds.
- The PDF's pages must be the panels, in the documented order. A PDF laid out as
  whole sides (outside and inside spreads) needs no imposition: print it Normal,
  double-sided, short edge.

**Placement.**
- Pages keep their orientation in a booklet or brochure; a landscape page in a
  portrait panel is scaled down to fit, not turned.
- Paper size is chosen for booklets and brochures. Normal jobs use the printer's
  default paper, as before.
- The margin is measured from the edge of the paper. If it is smaller than the
  printer's unprintable edge, the setup screen warns that the outer edge of the
  pages may be cut off.

**Rendering.** Imposed pages go through the same rendering as every other page
(§6): each page is rasterised once, directly from the PDF, at the job's resolution,
and scaled into its panel. There is no intermediate PDF and no second
rasterisation, but it is not vector imposition either - which, as in §6, costs
nothing on a raster inkjet and some sharpness on a PostScript printer.

**One setting per job.** Print settings are fixed when the job is created; a job
cannot be switched from single- to double-sided halfway through. If a printer
turns out not to accept a setting, the job waits with every batch and setting
intact; to print it differently, cancel it and set up a new one.
