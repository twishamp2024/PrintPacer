# PrintPacer — User guide

---

## What it does

Sends a long PDF to your printer in chunks, with a rest in between each one, as
**separate print jobs**. That last part is the point: a printer that jams
partway through one continuous 200-page job will happily manage four separate
50-page jobs spread across the night.

---

## Setting up a job

### 1. Add the PDF

Drag it onto the window, press **Browse…**, or right-click the file in File
Explorer and choose **Open with → PrintPacer**.

You will see the filename, page count, file size, paper size, PDF version and —
where the file has them — the title and author.

If the file is not a readable PDF, you are told why rather than being left with
a broken job: *"This file does not start with a PDF header, so it is not a PDF."*

### 2. Print settings: which pages, what layout, one or both sides

For an ordinary job there is nothing to do here: **All pages**, **Normal**,
**Single-sided** is how PrintPacer has always printed, and every new job starts
that way. The three choices are made in order, because each one shapes the next:

| Choice | Options |
|---|---|
| **Pages** | All pages · Odd pages only (1, 3, 5 …) · Even pages only (2, 4, 6 …) |
| **Layout** | Normal (one page per side) · Booklet · Brochure |
| **Sides** | Single-sided · Double-sided, then **Flip on**: long edge (turn like a book) or short edge (flip up like a notepad) |

Only the pages you select are printed — odd pages means the even pages never
reach the printer, and no blank pages are added in their place. Pages are
selected *before* the layout is worked out, so a booklet of odd pages is a
booklet made from pages 1, 3, 5 …

**Double-sided** is set on the printer itself for every batch — the Windows
setting the driver prints with — not just shown on screen. PrintPacer asks the
driver before the job starts and again before each batch, and reads the setting
back from the Windows queue as each batch arrives (*"Windows confirms batch 2 is
set to double-sided, flip on long edge."*). If the printer cannot print on both
sides, the setup screen says so in red under the print settings and **START
PRINTING** stays greyed out; nothing is ever printed single-sided in place of
double-sided.

> Some printers without a duplex unit (many home inkjets) still offer
> double-sided printing: their driver prints the fronts, then asks someone to
> turn the stack over. That works, but not while you are asleep.

#### Booklet

Pages are reordered and printed two to a side on landscape sheets, so that the
printed sheets, nested and folded down the middle, read in order. For eight
pages the sheets are:

```
Sheet 1   front  8 | 1    back  2 | 7
Sheet 2   front  6 | 3    back  4 | 5
```

You never have to work this out: the **Sheets** panel under the settings shows
the first few sheets for your document, and blank pages are added automatically
when the page count is not a multiple of four (shown as `·`).

| Option | What it does |
|---|---|
| **Binding** | Left for left-to-right text; Right for right-to-left text (every side is mirrored). |
| **Folded in** | All sheets in one booklet, or sections of 1–12 sheets. Thick booklets are easier to fold in sections: fold each section on its own and stack them in order. |
| **Keep the last page as the back cover** | Puts any blank pages just before the last page, instead of after it. |
| **Paper** | The sheet size. Must be one the printer offers; the setup screen checks. |
| **Margin** | Blank space at the edges of the sheet, in mm. |
| **Space at fold** | Blank space across the fold, in mm (half on each side of it). |
| **Page size** | *Fit each page to its panel*, or *Actual size* — printed at 100 % unless too big. |

A booklet is always double-sided, flipped on the short edge — which is right for a
landscape sheet folded down the middle. If a test sheet comes out with the backs
upside down, your driver interprets the flip the other way round: change **Flip
on** to long edge.

Sheets print outermost first: sheet 1 carries the front and back covers. Keep the
sheets in the order they came out, nest each one inside the one before, and fold.

#### Brochure

Each sheet is **one folded leaflet**, made from the next few pages of the PDF,
which are its panels. Unlike a booklet, sheets are never nested: an 8-page PDF as
a bi-fold brochure is two separate leaflets.

| Fold | Panels, as pages of the PDF | Printed |
|---|---|---|
| **Bi-fold** (2 panels per side) | 1 front cover · 2, 3 inside left and right · 4 back cover | outside `4 \| 1`, inside `2 \| 3` |
| **Tri-fold** (3 panels per side, letter fold) | 1 front cover · 2 flap that folds in · 3, 4, 5 inside, left to right · 6 back cover | outside `2 \| 6 \| 1`, inside `3 \| 4 \| 5` |

For a tri-fold, fold the right-hand inside panel in first, then close the front
cover over it. Paper, margin, space at each fold and page size work as for a
booklet. If your PDF already has each whole side of the brochure laid out (two
landscape pages, outside and inside), it does not need imposing: print it
**Normal**, **Double-sided**, flip on short edge.

#### Preview layout as PDF…

Draws the first sheets exactly as they will be printed — the same code that prints
them — into a PDF using *Microsoft Print to PDF*, and opens it. Fronts and backs
appear as consecutive pages. Nothing is sent to your printer.

#### What is set where

The layout (which page goes where, margins, scaling) is done by PrintPacer and
works on any printer. **Sides**, **paper** and **landscape orientation** are
printer settings: they depend on what the printer's driver accepts, which is why
PrintPacer checks them with the driver and shows the answer under the settings.

### 3. Choose the printer

Defaults to your Windows default printer. The line underneath shows the driver
and port, which is a quick way to confirm you have the right one when two
printers have similar names.

### 4. Print mode

**Colour** (the default) prints pages as they are. **Black & white** prints every
batch of the job in black and white: PrintPacer asks the printer's driver to use
black ink only, and sends every page in greyscale as well.

The line underneath says what the chosen printer will do:

- *The printer is asked to use black ink only* — the driver accepted the setting.
  Each batch's entry in the log confirms that Windows recorded it on the print
  job: *"Windows confirms batch 3 is set to black & white."*
- *This printer only prints in black & white* — nothing to set.
- A warning — the driver does not offer a black & white setting to Windows
  programs. The pages still go out in greyscale, but the printer may mix the grey
  from colour inks. If that matters, choose Greyscale or Black & white in the
  printer's own **Printing Preferences** as well. Each batch notes this in the log
  rather than claiming black and white.

The choice is made per job and is remembered as the starting point for the next
one; the dashboard shows the mode of the job that is running.

### 5. Pages per batch

**Start with 50.** If your printer still stalls, come down to 30 or 25.

The number is a ceiling, not a requirement — a 203-page document at 50 per batch
gives four batches of 50 and one of 3. Setting it larger than the document just
gives you one batch.

With **odd** or **even pages**, it counts the pages that print: 100 pages, odd
only, 10 per batch, is five batches of ten odd pages.

For anything **double-sided** — including every booklet and brochure — the box
is labelled **Sheets per batch**, and counts sheets of paper. Batches then always
hold whole sheets, both sides of each, so a sheet's front and back, or the two
halves of a booklet spread, can never end up in different print jobs. The hint
under the box says how many pages a sheet carries.

### 6. Rest between batches

**Start with 60 minutes.** This is the number that actually fixes the problem;
everything else is bookkeeping.

`0` sends the next batch as soon as the previous one is verified, which is useful
for testing but defeats the purpose in normal use.

### 7. Start later (optional)

Tick **Start later** and give a date and a 24-hour time to begin at, say, 23:30.

### 8. Print an invoice (optional)

Tick **Print an invoice when the job finishes** and enter the price of a black &
white page and of a colour page. Amounts use your Windows region's currency.

PrintPacer checks every page for colour as soon as you tick the box, and shows
what the bill will come to if every page prints:

```
Black & white     150 ×      ₹2.00  =      ₹300.00
Colour             50 ×     ₹10.00  =      ₹500.00
Total                                      ₹800.00

Colour pages: 151–200
```

A page counts as colour when it actually puts visible colour on paper — a red
heading or a photo does; black text that happens to be stored as RGB, or a grey
scan, does not. If the print mode is **Black & white**, every page is charged at
the black & white rate, because that is how it prints.

**START PRINTING** waits until the colour check has finished (a few seconds for
a few hundred pages). Your prices are remembered for next time.

When the last batch is verified, the invoice prints on the same printer as its
own one-page job. It charges only pages PrintPacer believes are on paper, and
lists the invoice number, dates, the black & white and colour page counts, the
rate and amount for each, the total, and which pages were colour.

The **Invoice** panel on the job screen shows the running total and whether the
invoice has printed. **Print invoice** appears once the job has ended: use it to
print another copy, to retry if the printer refused it, or to bill for the
pages of a job you cancelled part-way.

If PrintPacer stops while the invoice is being sent, it does not resend it by
itself on the next start — check the tray, and press **Print invoice again** if
it is not there.

### 9. Read "What will happen"

Before anything is sent, this panel spells out the actual plan:

```
Pages       200 pages
Layout      Normal, one page per side, on the printer's paper
Sides       Single-sided

4 separate print jobs of up to 50 pages, 60 minute(s) apart, every one in black & white.
    Batch 1:  Pages 1–50          Sat 23:00
    Batch 2:  Pages 51–100        Sun 00:00
    Batch 3:  Pages 101–150       Sun 01:00
    Batch 4:  Pages 151–200       Sun 02:00
```

and for a booklet:

```
Pages       14 pages on 4 sheets, 2 blank pages added
Layout      Booklet, bound on the left, 2 pages per side, A4 landscape
Sides       Double-sided, flip on short edge
Spacing     6 mm margins, 12 mm at each fold, fit to panel, pages at about 65%

2 separate print jobs of up to 2 sheets, 60 minute(s) apart, every one in colour.
    Batch 1:  Sheets 1–2 · pages 1–4, 13–14   Sat 23:00
    Batch 2:  Sheets 3–4 · pages 5–12         Sun 00:00
```

If that is not what you expected, fix it now.

### 10. Press START PRINTING

---

## The job screen

```
textbook.pdf                                      [ Resting the printer ]
200 pages · 4 batches of up to 50 · 60 min apart · HP Ink Tank

100 / 200 pages                              Next batch in  42 min 18 s
████████████████████░░░░░░░░░░░░░░░░░░░░

CURRENT BATCH                    NEXT BATCH
Batch 2 of 4 · Pages 51–100      Batch 3 · Pages 101–150

BATCHES
✓  Batch 1   Pages 1–50      Printed
✓  Batch 2   Pages 51–100    Printed
○  Batch 3   Pages 101–150   Waiting
○  Batch 4   Pages 151–200   Waiting
```

At the bottom: live printer status, and — if your printer can be reached on the
network — what the printer itself says about its state and its page counter.

---

## Leaving it running overnight

**Close the window.** The job keeps running from the notification area, near the
clock. Double-click the icon to bring it back.

To exit properly, right-click the tray icon → **Exit**. It will warn you if a job
is still running.

**Keep the computer awake** (Settings, on by default) holds a Windows power
request for the duration. The **display is still allowed to switch off** and you
can close a laptop lid — only the system is kept from sleeping. It is released
as soon as there is nothing left to do.

Time spent asleep or hibernating still counts towards the rest period, so a
60-minute rest that spans a sleep is still 60 minutes.

**Start PrintPacer when I sign in to Windows** (Settings) makes an interrupted
job pick itself up after a restart without you being there.

---

## Controls

| Button | What it does |
|---|---|
| **Pause** | Stops the *next* batch being sent |
| **Resume** | Carries on |
| **Cancel queued batch** | Removes the batch currently in the Windows queue |
| **Cancel job** | Stops everything; remaining batches are marked cancelled |
| **Start a new job** | Available once a job has finished |

### What Pause can and cannot do

Pause stops the next batch being **sent**. A batch already in the printer's queue
will finish, because Windows cannot reliably un-send pages that have already
reached the printer. The application says so on screen rather than implying
otherwise.

To stop that one too, use **Cancel queued batch** — then tell PrintPacer what
actually came out (see below).

---

## When something needs your decision

PrintPacer asks instead of guessing. The three cases:

### "Needs checking" on a batch

Shown with the reason. For example:

> The printer produced 18 of 50 sheets, so the batch stopped part-way. Check the
> output tray: the pages after the first 18 of this batch are the ones still
> needed. The printer recorded 1 paper jam during this batch.

Look at the paper, then choose:

| Button | Use when |
|---|---|
| **These pages did print** | They are all there. The batch is accepted and printing continues. |
| **They did not — send again** | Nothing came out. The batch is resent. |
| **Keep the 18 that printed, send the rest** | Some came out. Only the missing pages are sent, as a new batch. |

The third option is the one that saves paper. It appears whenever the printer's
own counter can tell PrintPacer how far the batch got.

You are always asked to confirm before anything is resent, with a reminder that
pages which *did* print will print a second time.

### "Printer unavailable"

Offline, paused, out of paper, door open. **Nothing is sent.** PrintPacer
re-checks every 5 minutes (configurable) and continues on its own when the
printer comes back. It does not pile up repeated jobs while it waits.

If the printer stays unavailable for 4 hours, it stops and asks, rather than
retrying silently until morning.

### "Printing has stalled"

A batch stopped making progress — the exact failure this application exists to
manage. PrintPacer tells you how many sheets came out and **does not resend
anything**. Clear the jam, then use the buttons above to say what happened.

---

## If the application closes unexpectedly

Reopen it. You will get a summary of what was interrupted.

If nothing was mid-print, it simply says so and continues from the next unsent
batch. **No page is printed twice.**

If a batch *was* in the printer's queue when it stopped, PrintPacer works out
what it can from the printer's own page counter:

- Counter advanced by exactly the batch size → **it printed**, and the job
  continues (after the normal rest period).
- Counter never moved → **it did not print**, and the batch is resent.
- Counter moved partially, or the printer has no reachable counter → **you are
  asked**, and nothing is sent until you answer.

Closing the application never loses a job. The plan, the progress and the
evidence are all on disk.

---

## Settings

Most people never need these.

**Overnight**
- Keep the computer awake while a job is running *(on)*
- Continue unfinished jobs automatically when PrintPacer starts *(on)* — applies
  only to jobs whose interrupted batches could be resolved with certainty;
  anything in doubt always waits for you
- Start PrintPacer when I sign in to Windows *(off)*
- Start minimised to the notification area *(off)*

**Notifications** *(all on)* — batch finished, printer needs attention, document
finished. Deliberately sparse: a blocked printer notifies at most every 30 minutes.

**Printing**
- Render resolution *(300 dpi)* — higher is slower and uses much more memory.
  300 dpi matches what an inkjet actually puts on plain paper.

**When something goes wrong**
- Re-check the printer every *5* minutes
- Give up waiting after *240* minutes (0 = never)
- Treat a batch as stalled after *45* minutes without progress
- Automatic retries per batch *(3)*

> A batch is only ever resent automatically when the evidence shows that nothing
> printed. If the outcome is uncertain, PrintPacer stops and asks, **no matter
> what these numbers say.**

**Reading the printer directly** — talk to the printer over the network for its
real status and physical page counter. Leave the address blank to find it
automatically, or type an IP if discovery fails. Turning this off still works,
but interrupted batches then have to be confirmed by hand more often.

**Storage** — log retention, and a link to your data folder.

---

## Logs

- **Logs** in the header opens the folder (`%LOCALAPPDATA%\PrintPacer\logs`).
- **Export log…** on the job screen writes a full account of the job: every
  batch, every attempt, the spooler job ids, the printer counter before and
  after, and the reasoning behind every verdict.

The on-screen **Activity** list is the same story in brief:

```
23:00:04  Job created for textbook.pdf
23:00:05  Batch 1 of 4 submitted (Pages 1–50).
23:12:41  Batch 1 completed (Pages 1–50).
23:12:41  Resting the printer for 60 minutes.
00:12:42  Batch 2 of 4 submitted (Pages 51–100).
```

---

## Troubleshooting

**The printer isn't found.** Check it is installed in Windows. Use **Printer
settings** in the header. If the queue was removed while a job was running,
PrintPacer stops and says so rather than failing silently.

**"Printer not answering on the network."** Normal if it has gone to sleep — it
wakes when the next batch arrives. If it never answers, set its IP manually in
**Settings → Reading the printer directly**; find it on the printer's own network
report or in your router's device list. Without it, everything still works, you
are just asked to confirm interrupted batches more often.

**Pages look wrong.** Try a higher render resolution in Settings. Check the
document prints correctly from your usual PDF reader first.

**"The PDF file has changed since this job was set up."** The file was edited or
replaced. Page 51 of a different document is not page 51, so PrintPacer stops.
Cancel the job and start again with the new file.

**A batch keeps failing.** After 3 attempts it stops and asks. Check the log
export for what the printer reported each time — jam and paper-feed counts are
recorded there.

**Nothing prints and there is no error.** Open **Print queue** from the header.
If jobs are sitting there unprocessed, the Windows spooler itself may be stuck;
restarting the Print Spooler service usually clears it.

---

## What PrintPacer deliberately does not do

It does not claim to restart your printer. Turning an HP Ink Tank off and on is
a physical act; nothing in software can do it, and pretending otherwise by
restarting the Windows spooler would be dishonest. When a batch stalls,
PrintPacer tells you what it knows and waits.

The full list is in [LIMITATIONS.md](LIMITATIONS.md).
