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

### 2. Choose the printer

Defaults to your Windows default printer. The line underneath shows the driver
and port, which is a quick way to confirm you have the right one when two
printers have similar names.

### 3. Pages per batch

**Start with 50.** If your printer still stalls, come down to 30 or 25.

The number is a ceiling, not a requirement — a 203-page document at 50 per batch
gives four batches of 50 and one of 3. Setting it larger than the document just
gives you one batch.

### 4. Rest between batches

**Start with 60 minutes.** This is the number that actually fixes the problem;
everything else is bookkeeping.

`0` sends the next batch as soon as the previous one is verified, which is useful
for testing but defeats the purpose in normal use.

### 5. Start later (optional)

Tick **Start later** and give a date and a 24-hour time to begin at, say, 23:30.

### 6. Read "What will happen"

Before anything is sent, this panel spells out the actual plan:

```
4 separate print jobs, 60 minute(s) apart.
    Batch 1:  Pages 1–50          Sat 23:00
    Batch 2:  Pages 51–100        Sun 00:00
    Batch 3:  Pages 101–150       Sun 01:00
    Batch 4:  Pages 151–200       Sun 02:00
```

If that is not what you expected, fix it now.

### 7. Press START PRINTING

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
- Render in greyscale *(off)*

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
