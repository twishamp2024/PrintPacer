using FluentAssertions;
using Microsoft.Data.Sqlite;
using PrintPacer.Core.Billing;
using PrintPacer.Core.Model;
using PrintPacer.Core.Persistence;
using PrintPacer.Core.Printing;
using Xunit;

namespace PrintPacer.Tests;

/// <summary>
/// Page selection, double-sided printing, booklets and brochures running through the real
/// engine: batching, rests, the queue, verification against the printer's counter, restarts,
/// the job database and the invoice. The simulated printer records every request exactly as
/// the engine made it, so these tests check what each batch actually asked to print.
/// </summary>
public class PrintLayoutEngineTests
{
    private static readonly PrintSettings OddPages = new() { PageSelection = PageSelection.OddPages };
    private static readonly PrintSettings EvenPages = new() { PageSelection = PageSelection.EvenPages };
    private static readonly PrintSettings LongEdge = new() { Duplex = DuplexMode.LongEdge };

    private static readonly PrintSettings Booklet = new()
    {
        Layout = PrintLayout.Booklet, Duplex = DuplexMode.ShortEdge, Paper = PaperChoice.A4, MarginMm = 8, FoldGapMm = 10,
    };

    private static readonly PrintSettings TriFold = new()
    {
        Layout = PrintLayout.Brochure, Fold = BrochureFold.TriFold, Duplex = DuplexMode.ShortEdge, Scaling = PageScaling.ShrinkOnly,
    };

    // ---- Odd and even pages -------------------------------------------------------------------

    [Fact]
    public async Task AnOddPageJobPrintsOnlyOddPagesAcrossBatchesAndRests()
    {
        await using var h = await EngineHarness.CreateAsync(20);
        var job = await h.CreateJobAsync(pagesPerBatch: 3, delayMinutes: 30, printSettings: OddPages);

        job.PrintSettings.Should().Be(OddPages);
        job.SelectedPageCount.Should().Be(10);
        job.UnitCount.Should().Be(10);
        job.BatchCount.Should().Be(4);
        job.Batches.Select(b => b.RangeLabel).Should().Equal(
            "Odd pages 1–5", "Odd pages 7–11", "Odd pages 13–17", "Page 19");

        await RunToCompletionAsync(h, job.Id, restMinutes: 30);

        var printed = h.PrintEngine.SubmittedRequests.SelectMany(r => r.EffectiveSides.SelectMany(s => s.SourcePages)).ToList();
        printed.Should().Equal(1, 3, 5, 7, 9, 11, 13, 15, 17, 19);
        h.PrintEngine.SubmittedRequests.Should().OnlyContain(r => r.Imposition == null && r.Setup == PrinterSetup.Default);
        h.Printer.TotalPagesPrinted.Should().Be(10, "no even page and no blank page reached the printer");

        h.Job.PagesDone.Should().Be(10);
        h.Events().Should().Contain(e => e.Message == "Print settings: Odd pages · Single-sided.");
        h.Events().Should().Contain(e => e.Message == "Job complete: all 10 pages of fixture-20.pdf printed.");
    }

    [Fact]
    public async Task AnEvenPageJobOfAnOddLengthDocumentStopsAtTheLastEvenPage()
    {
        await using var h = await EngineHarness.CreateAsync(9);
        var job = await h.CreateJobAsync(pagesPerBatch: 10, delayMinutes: 0, printSettings: EvenPages);

        job.BatchCount.Should().Be(1);
        await RunToCompletionAsync(h, job.Id);

        h.PrintEngine.SubmittedRequests.Single().EffectiveSides.Select(s => s.Panels.Single())
            .Should().Equal(2, 4, 6, 8);
    }

    [Fact]
    public async Task ADocumentWithNoPagesOfTheChosenKindIsRefusedUpFront()
    {
        await using var h = await EngineHarness.CreateAsync(1);
        var act = () => h.CreateJobAsync(pagesPerBatch: 10, delayMinutes: 0, printSettings: EvenPages);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*no even pages*");
        (await h.JobsAsync()).Should().BeEmpty();
    }

    // ---- Double-sided ---------------------------------------------------------------------------

    [Fact]
    public async Task ADoubleSidedJobBatchesWholeSheetsAndAsksForDuplexOnEveryBatch()
    {
        await using var h = await EngineHarness.CreateAsync(9);
        var job = await h.CreateJobAsync(pagesPerBatch: 2, delayMinutes: 20, printSettings: LongEdge);

        job.Unit.Should().Be(PrintUnit.Sheet);
        job.UnitCount.Should().Be(5);
        job.Batches.Select(b => b.Impressions).Should().Equal(4, 4, 1);

        await RunToCompletionAsync(h, job.Id, restMinutes: 20);

        var requests = h.PrintEngine.SubmittedRequests;
        requests.Should().HaveCount(3).And.OnlyContain(r => r.Setup.Duplex == DuplexMode.LongEdge);
        requests.Select(r => r.PageCount).Should().Equal(4, 4, 1);
        requests.SelectMany(r => r.EffectiveSides).Select(s => (s.Sheet, s.Face, s.Panels[0])).Should().Equal(
            (1, SheetFace.Front, (int?)1), (1, SheetFace.Back, 2), (2, SheetFace.Front, 3), (2, SheetFace.Back, 4),
            (3, SheetFace.Front, 5), (3, SheetFace.Back, 6), (4, SheetFace.Front, 7), (4, SheetFace.Back, 8),
            (5, SheetFace.Front, 9));

        // The counter counts printed sides; every batch verified against its own side count.
        h.Printer.TotalPagesPrinted.Should().Be(9);
        h.Job.Batches.Should().OnlyContain(b => b.State == BatchState.Completed);
        h.Events().Count(e => e.Message.StartsWith("Windows confirms batch") &&
                              e.Message.EndsWith("double-sided, flip on long edge."))
            .Should().Be(3, "the queued job's own DEVMODE says double-sided for every batch");
        h.Events().Should().Contain(e => e.Message == "Job complete: all 9 pages, on 5 sheets, of fixture-9.pdf printed.");
    }

    [Fact]
    public async Task APrinterThatCannotPrintDoubleSidedStopsTheJobWithoutSendingOrRetrying()
    {
        await using var h = await EngineHarness.CreateAsync(12);
        h.PrintEngine.CannotDuplex = true;

        var job = await h.CreateJobAsync(pagesPerBatch: 3, delayMinutes: 10, printSettings: LongEdge);
        h.Engine.StartJob(job.Id);

        (await h.WaitForStateAsync(JobState.NeedsAttention)).Should().BeTrue(h.Diagnostics());
        h.Job.AttentionReason.Should().Contain("cannot print on both sides").And.Contain("Resume");
        h.Job.Batches.Should().OnlyContain(b => b.State == BatchState.Pending, "nothing printed, so nothing is lost");
        h.Printer.TotalPagesPrinted.Should().Be(0);
        h.Printer.Snapshot().Should().BeEmpty("nothing reached the Windows queue");

        // Not retried on a timer: the same driver would refuse again.
        h.SkipDelay(120);
        await h.WaitUntilAsync(() => false, 400);
        h.PrintEngine.SubmissionCount.Should().Be(1);
        h.Job.State.Should().Be(JobState.NeedsAttention);

        // The settings are kept; once the printer can do it, Resume carries on.
        h.Job.PrintSettings.Should().Be(LongEdge);
        h.PrintEngine.CannotDuplex = false;
        await RunToCompletionAsync(h, job.Id, restMinutes: 10, resume: true);
        h.Printer.TotalPagesPrinted.Should().Be(12);
    }

    [Fact]
    public async Task ADriverThatQuietlyDropsDuplexIsCaughtFromTheQueue()
    {
        await using var h = await EngineHarness.CreateAsync(4);
        h.PrintEngine.QueueShowsSingleSided = true;

        var job = await h.CreateJobAsync(pagesPerBatch: 10, delayMinutes: 0, printSettings: LongEdge);
        await RunToCompletionAsync(h, job.Id);

        h.Events().Should().Contain(e => e.Level == "Warning" &&
            e.Message == "Windows shows batch 1 set to single-sided, not double-sided, flip on long edge.");
        h.Notifier.Notifications.Should().Contain(n => n.Title.Contains("may not be double-sided"));
    }

    // ---- Booklet ------------------------------------------------------------------------------

    [Fact]
    public async Task ABookletJobSendsWholeImposedSheetsBatchByBatch()
    {
        await using var h = await EngineHarness.CreateAsync(14);
        var job = await h.CreateJobAsync(pagesPerBatch: 2, delayMinutes: 45, printSettings: Booklet);

        job.UnitCount.Should().Be(4, "14 pages pad to 16: four sheets");
        job.BatchCount.Should().Be(2);
        job.SizeSummary.Should().Be("14 pages on 4 sheets, 2 blank pages added");
        job.Batches[0].RangeLabel.Should().Be("Sheets 1–2 · pages 1–4, 13–14");

        await RunToCompletionAsync(h, job.Id, restMinutes: 45);

        var requests = h.PrintEngine.SubmittedRequests;
        requests.Should().HaveCount(2);
        requests.Should().OnlyContain(r =>
            r.Setup == new PrinterSetup(DuplexMode.ShortEdge, PaperChoice.A4, true) &&
            r.Imposition == new ImpositionOptions(8, 10, PageScaling.FitToPanel));

        // Each batch is complete sheets, front then back, and together they are the booklet.
        var plan = PrintPlan.Build(Booklet, 14);
        requests.SelectMany(r => r.Sides!).Should().Equal(plan.Sheets.SelectMany(s => s.Sides));
        requests[0].Sides!.Select(s => s.Describe()).Should().Equal("· | 1", "2 | ·", "14 | 3", "4 | 13");
        requests.Should().OnlyContain(r => r.Sides!.Count % 2 == 0, "a sheet's front and back never go to different jobs");

        // Verified against the printer's counter by printed sides: two per sheet.
        h.Job.Batches.Should().OnlyContain(b => b.State == BatchState.Completed && b.Impressions == 4);
        h.Printer.TotalPagesPrinted.Should().Be(8);
        h.Job.PagesDone.Should().Be(4, "progress counts sheets for a booklet");
        h.Events().Should().Contain(e => e.Message.StartsWith("Print settings: All pages · Booklet (bound on the left)"));
    }

    [Fact]
    public async Task ABookletSurvivesARestartWithItsLayoutIntact()
    {
        await using var h = await EngineHarness.CreateAsync(24);
        var settings = Booklet with { Binding = BookletBinding.Right, SheetsPerSignature = 3, KeepLastPageAsBackCover = true, MarginMm = 6.35 };
        var job = await h.CreateJobAsync(pagesPerBatch: 2, delayMinutes: 60, printSettings: settings);

        h.Engine.StartJob(job.Id);
        (await h.WaitForBatchStateAsync(1, BatchState.Completed)).Should().BeTrue(h.Diagnostics());
        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue();
        var firstBatch = h.PrintEngine.SubmittedRequests.Single();

        await h.RestartApplicationAsync();

        h.Job.PrintSettings.Should().Be(settings, "every print setting comes back from the database");
        h.Job.Batches.Select(b => b.RangeLabel).Should().Equal(job.Batches.Select(b => b.RangeLabel));

        await RunToCompletionAsync(h, job.Id, restMinutes: 60, alreadyStarted: true);

        var plan = PrintPlan.Build(settings, 24);
        new[] { firstBatch }.Concat(h.PrintEngine.SubmittedRequests).SelectMany(r => r.Sides!)
            .Should().Equal(plan.Sheets.SelectMany(s => s.Sides), "the batches after the restart continue the same booklet");
        h.PrintEngine.SubmittedRequests.Should().OnlyContain(r =>
            r.Setup == firstBatch.Setup && r.Imposition == firstBatch.Imposition);
    }

    [Fact]
    public async Task AnInterruptedSheetBatchIsSettledByCountingPrintedSides()
    {
        await using var h = await EngineHarness.CreateAsync(16);
        h.Printer.PagesPerAdvance = 1;
        var job = await h.CreateJobAsync(pagesPerBatch: 4, delayMinutes: 60, printSettings: Booklet);

        h.Engine.StartJob(job.Id);
        (await h.WaitUntilAsync(() => h.Job.Batches[0].SpoolerPagesPrinted >= 2)).Should().BeTrue(h.Diagnostics());

        // Power cut mid-batch; the printer finishes the job, and Windows clears it from the
        // queue, while the application is down.
        await h.Engine.SimulateAbruptTerminationAsync();
        h.Printer.RunToIdle();
        h.Printer.Advance(h.Printer.FinishedLingerAdvances + 1);
        await h.RestartApplicationAsync(graceful: false);

        h.PrintEngine.SubmissionCount.Should().Be(0, "a batch that may have printed is never resent by itself");
        h.Job.Batches[0].State.Should().Be(BatchState.Completed,
            "the counter advanced by exactly the batch's 8 printed sides: " + h.Diagnostics());
    }

    // ---- Brochure -------------------------------------------------------------------------------

    [Fact]
    public async Task ABrochureJobPrintsOneLeafletPerSheet()
    {
        await using var h = await EngineHarness.CreateAsync(12);
        var job = await h.CreateJobAsync(pagesPerBatch: 1, delayMinutes: 0, printSettings: TriFold);

        job.BatchCount.Should().Be(2);
        await RunToCompletionAsync(h, job.Id);

        var sides = h.PrintEngine.SubmittedRequests.Select(r => string.Join("  /  ", r.Sides!.Select(s => s.Describe())));
        sides.Should().Equal("2 | 6 | 1  /  3 | 4 | 5", "8 | 12 | 7  /  9 | 10 | 11");
        h.PrintEngine.SubmittedRequests.Should().OnlyContain(r =>
            r.Imposition!.Scaling == PageScaling.ShrinkOnly && r.Setup.Landscape == true);
    }

    // ---- Invoice ---------------------------------------------------------------------------------

    [Fact]
    public async Task TheInvoiceChargesOnlyTheDocumentPagesThatPrinted()
    {
        await using var h = await EngineHarness.CreateAsync(6, colourPages: new[] { 1, 2, 3 });

        var job = await h.CreateJobWithInvoiceAsync(pagesPerBatch: 10, delayMinutes: 0, blackWhitePrice: 1m,
            colourPrice: 10m, printSettings: OddPages);
        await RunToCompletionAsync(h, job.Id);

        // Pages 1, 3 and 5 printed; 1 and 3 are colour. Pages 2, 4 and 6 never printed.
        var invoice = h.InvoicePrinter.Printed.Should().ContainSingle().Subject;
        invoice.ColourPages.Should().Be(2);
        invoice.BlackWhitePages.Should().Be(1);
        invoice.ColourPageRanges.Should().Be("1, 3");
        invoice.Total.Should().Be(21m);
    }

    [Fact]
    public async Task ABookletInvoiceChargesPagesNotSheetsOrBlanks()
    {
        await using var h = await EngineHarness.CreateAsync(5);

        var job = await h.CreateJobWithInvoiceAsync(pagesPerBatch: 1, delayMinutes: 0, blackWhitePrice: 2m,
            colourPrice: 5m, printSettings: Booklet);
        await RunToCompletionAsync(h, job.Id);

        h.InvoicePrinter.Printed.Should().ContainSingle().Which.PagesPrinted.Should().Be(5, "two sheets, five pages, three blanks");
    }

    // ---- The job database ----------------------------------------------------------------------

    [Fact]
    public void EveryPrintSettingIsStoredWithTheJob()
    {
        var path = Path.Combine(TempDirectory(), "jobs.db");
        var settings = new PrintSettings
        {
            PageSelection = PageSelection.EvenPages, Layout = PrintLayout.Booklet, Duplex = DuplexMode.LongEdge,
            Paper = PaperChoice.Legal, MarginMm = 6.35, FoldGapMm = 3.2, Scaling = PageScaling.ShrinkOnly,
            Binding = BookletBinding.Right, SheetsPerSignature = 5, KeepLastPageAsBackCover = true, Fold = BrochureFold.TriFold,
        };

        using (var store = new JobStore(path))
        {
            store.InsertJob(Job("advanced", 40, settings, pagesPerBatch: 2));
            store.InsertJob(Job("plain", 40, PrintSettings.Default, pagesPerBatch: 10));
        }

        using (var store = new JobStore(path))
        {
            var jobs = store.LoadAllJobs().ToDictionary(j => j.Id);
            jobs["advanced"].PrintSettings.Should().Be(settings);
            jobs["plain"].PrintSettings.Should().Be(PrintSettings.Default);

            // The plan is rebuilt on load, so the batches mean the same sheets as before.
            jobs["advanced"].Batches[0].RangeLabel.Should().Be("Sheets 1–2 · even pages 2–8, 34–40");
            jobs["plain"].Batches[0].RangeLabel.Should().Be("Pages 1–10");
        }
    }

    [Fact]
    public void JobsFromBeforePrintSettingsReadBackAsPlainSingleSidedJobs()
    {
        var path = Path.Combine(TempDirectory(), "jobs.db");
        using (var store = new JobStore(path))
            store.InsertJob(Job("old", 30, PrintSettings.Default, pagesPerBatch: 10));

        // Strip the database back to what version 1.2 wrote: schema v2, no print settings.
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE jobs DROP COLUMN page_selection;
                ALTER TABLE jobs DROP COLUMN layout;
                ALTER TABLE jobs DROP COLUMN duplex;
                ALTER TABLE jobs DROP COLUMN paper;
                ALTER TABLE jobs DROP COLUMN margin_mm;
                ALTER TABLE jobs DROP COLUMN fold_gap_mm;
                ALTER TABLE jobs DROP COLUMN scaling;
                ALTER TABLE jobs DROP COLUMN booklet_binding;
                ALTER TABLE jobs DROP COLUMN sheets_per_section;
                ALTER TABLE jobs DROP COLUMN keep_back_cover;
                ALTER TABLE jobs DROP COLUMN brochure_fold;
                ALTER TABLE jobs DROP COLUMN job_number;
                ALTER TABLE schema_info DROP COLUMN next_job_number;
                UPDATE schema_info SET version = 2;
                """;
            command.ExecuteNonQuery();
        }

        using (var store = new JobStore(path))
        {
            var job = store.LoadAllJobs().Single();
            job.PrintSettings.Should().Be(PrintSettings.Default);
            job.Plan.Unit.Should().Be(PrintUnit.Page);
            job.Batches.Select(b => b.RangeLabel).Should().Equal("Pages 1–10", "Pages 11–20", "Pages 21–30");
            job.Batches.Select(b => b.Impressions).Should().Equal(10, 10, 10);
        }
    }

    [Fact]
    public void DamagedPrintSettingsFallBackToDefaultsInsteadOfBreakingTheLoad()
    {
        var path = Path.Combine(TempDirectory(), "jobs.db");
        using (var store = new JobStore(path))
            store.InsertJob(Job("odd", 10, new PrintSettings { Duplex = DuplexMode.ShortEdge }, pagesPerBatch: 2));

        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET page_selection = 9, scaling = -3, margin_mm = 'lots';";
            command.ExecuteNonQuery();
        }

        using (var store = new JobStore(path))
        {
            var settings = store.LoadAllJobs().Single().PrintSettings;
            settings.PageSelection.Should().Be(PageSelection.AllPages);
            settings.Scaling.Should().Be(PageScaling.FitToPanel);
            settings.MarginMm.Should().Be(PrintSettings.Default.MarginMm);
            settings.Duplex.Should().Be(DuplexMode.ShortEdge, "undamaged settings are kept");
        }
    }

    [Fact]
    public void AnImpossibleStoredCombinationStillLoadsAndDisplaysButCannotPlan()
    {
        var path = Path.Combine(TempDirectory(), "jobs.db");
        using (var store = new JobStore(path))
            store.InsertJob(Job("broken", 12, Booklet, pagesPerBatch: 2));

        // A booklet cannot be single-sided; only a hand-edited database could say so.
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET duplex = 0;";
            command.ExecuteNonQuery();
        }

        using (var store = new JobStore(path))
        {
            var job = store.LoadAllJobs().Single();
            job.TryGetPlan().Should().BeNull();

            var snapshot = PrintPacer.Core.Engine.JobSnapshot.From(job);
            snapshot.SizeSummary.Should().Contain("print settings unreadable");
            snapshot.Batches.Should().HaveCount(2, "the job and its batches are kept, not lost");
        }
    }

    [Fact]
    public void TheLastBookletDetailsAreRememberedButEveryJobStartsPlain()
    {
        var path = Path.Combine(TempDirectory(), "settings.json");
        var store = new SettingsStore(path);

        store.Load().LayoutDefaults.Should().Be(PrintSettings.Default, "no settings file yet");

        store.Save(new AppSettings
        {
            LayoutDefaults = new PrintSettings
            {
                PageSelection = PageSelection.OddPages, Layout = PrintLayout.Booklet, Duplex = DuplexMode.ShortEdge,
                Paper = PaperChoice.A3, MarginMm = 9.5, FoldGapMm = 20, Binding = BookletBinding.Right,
                SheetsPerSignature = 4, KeepLastPageAsBackCover = true, Fold = BrochureFold.TriFold,
                Scaling = PageScaling.ShrinkOnly,
            },
        });

        File.ReadAllText(path).Should().Contain("\"paper\": \"A3\"", "enums are stored as names, readable by hand");

        var remembered = store.Load().LayoutDefaults;
        remembered.Should().Be(new PrintSettings
        {
            Paper = PaperChoice.A3, MarginMm = 9.5, FoldGapMm = 20, Binding = BookletBinding.Right,
            SheetsPerSignature = 4, KeepLastPageAsBackCover = true, Fold = BrochureFold.TriFold,
            Scaling = PageScaling.ShrinkOnly,
        }, "odd pages, booklet and double-sided are never carried over to the next job");

        File.WriteAllText(path, """{ "layoutDefaults": { "marginMm": 500, "sheetsPerSignature": -2 } }""");
        store.Load().LayoutDefaults.MarginMm.Should().Be(PrintSettings.MaxMarginMm);
        store.Load().LayoutDefaults.SheetsPerSignature.Should().Be(0);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    /// <summary>Starts (or resumes) a job and lets it run to completion, skipping every rest.</summary>
    private static async Task RunToCompletionAsync(EngineHarness h, string jobId, int restMinutes = 0,
        bool resume = false, bool alreadyStarted = false)
    {
        if (resume) h.Engine.ResumeJob(jobId);
        else if (!alreadyStarted) h.Engine.StartJob(jobId);

        for (var i = 0; i < 40 && h.Job.State != JobState.Completed; i++)
        {
            await h.WaitUntilAsync(() => h.Job.State is JobState.Completed or JobState.CoolingDown, 5_000);
            if (h.Job.State != JobState.CoolingDown) continue;

            // Skip the rest, then let the engine notice and send the next batch before looking again.
            h.SkipDelay(restMinutes + 1);
            await h.WaitUntilAsync(() => h.Job.State != JobState.CoolingDown, 5_000);
        }

        h.Job.State.Should().Be(JobState.Completed, h.Diagnostics());
        if (h.Job.WantsInvoice)
        {
            // The invoice is sent after the last batch, on its own.
            (await h.WaitUntilAsync(() => h.Job.InvoiceState == InvoiceState.Printed)).Should().BeTrue(h.Diagnostics());
        }
    }

    private static PrintJob Job(string id, int pages, PrintSettings settings, int pagesPerBatch)
    {
        var job = new PrintJob
        {
            Id = id,
            SourcePath = "C:\\nowhere\\book.pdf",
            FileName = "book.pdf",
            FileSha256 = "00",
            TotalPages = pages,
            PrinterName = "Printer",
            PagesPerBatch = pagesPerBatch,
            PrintSettings = settings,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        job.Batches.AddRange(PrintJob.PlanBatches(id, job.Plan, pagesPerBatch));
        return job;
    }

    private static string TempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "printpacer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
