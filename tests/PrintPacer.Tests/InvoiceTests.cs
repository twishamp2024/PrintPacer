using System.Drawing;
using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using PrintPacer.Core.Billing;
using PrintPacer.Core.Model;
using PrintPacer.Core.Pdf;
using PrintPacer.Core.Persistence;
using PrintPacer.Core.Printing;
using PrintPacer.Core.Simulation;
using Xunit;

namespace PrintPacer.Tests;

/// <summary>
/// The optional invoice: which pages count as colour, what the bill adds up to, and that
/// the engine prints it exactly once at the end - including across a crash.
/// </summary>
public class InvoiceTests
{
    // ---- Colour detection ------------------------------------------------------

    [Fact]
    public void ColourCheckFindsExactlyThePagesThatCarryColour()
    {
        var root = TempDirectory();
        var path = Path.Combine(root, "mixed.pdf");
        TestPdfBuilder.Write(path, 8, "Mixed", colourPages: new[] { 2, 5, 6 });

        var pages = PageColourAnalyzer.Analyse(path);

        // The fixture's black text, dark-grey border and light-grey diagonals must all read as
        // black and white; only the red block may count as colour.
        pages.Select((colour, i) => (Page: i + 1, colour)).Where(p => p.colour).Select(p => p.Page)
            .Should().Equal(2, 5, 6);
    }

    [Fact]
    public void AFewStrayColouredPixelsDoNotMakeAPageColour()
    {
        const int pixels = 240_000;
        var bgra = new byte[pixels * 4];
        for (var i = 0; i < 10; i++)
        {
            bgra[i * 4 + 2] = 255;  // red
            bgra[i * 4 + 3] = 255;  // opaque
        }

        PageColourAnalyzer.IsColour(bgra, pixels).Should().BeFalse();

        for (var i = 0; i < 200; i++)
        {
            bgra[i * 4 + 2] = 255;
            bgra[i * 4 + 3] = 255;
        }

        PageColourAnalyzer.IsColour(bgra, pixels).Should().BeTrue();
    }

    [Fact]
    public void PageRangesAreWrittenAsAReaderWould()
    {
        PageColourMap.DescribeRanges(new[] { 1, 3, 4, 5, 9, 10 }).Should().Be("1, 3–5, 9–10");
        PageColourMap.DescribeRanges(Array.Empty<int>()).Should().BeEmpty();
        PageColourMap.Shorten("1, 3, 5, 7, 9", 7).Should().Be("1, 3, 5, …");
    }

    // ---- The bill --------------------------------------------------------------

    [Fact]
    public void ChargesEachPrintedPageAtItsOwnRate()
    {
        var job = Job(totalPages: 10, map: "BBCBBCCBBB", blackWhite: 2m, colour: 10.5m);
        foreach (var b in job.Batches) b.State = BatchState.Completed;

        var invoice = Invoice.For(job, DateTimeOffset.UtcNow);

        invoice.BlackWhitePages.Should().Be(7);
        invoice.ColourPages.Should().Be(3);
        invoice.BlackWhiteAmount.Should().Be(14m);
        invoice.ColourAmount.Should().Be(31.5m);
        invoice.Total.Should().Be(45.5m);
        invoice.ColourPageRanges.Should().Be("3, 6–7");
    }

    [Fact]
    public void OnlyPagesThatPrintedAreCharged()
    {
        var job = Job(totalPages: 10, map: "CCCCCBBBBB", blackWhite: 1m, colour: 5m);
        job.Batches[0].State = BatchState.Completed;   // pages 1-5, all colour
        job.Batches[1].State = BatchState.Cancelled;   // pages 6-10 never went out

        var invoice = Invoice.For(job, DateTimeOffset.UtcNow);

        invoice.PagesPrinted.Should().Be(5);
        invoice.ColourPages.Should().Be(5);
        invoice.BlackWhitePages.Should().Be(0);
        invoice.Total.Should().Be(25m);
    }

    [Fact]
    public void BlackAndWhiteJobsChargeEveryPageAsBlackAndWhite()
    {
        var job = Job(totalPages: 4, map: "CCCC", blackWhite: 2m, colour: 10m, colourMode: ColourMode.BlackAndWhite);
        foreach (var b in job.Batches) b.State = BatchState.Completed;

        var invoice = Invoice.For(job, DateTimeOffset.UtcNow);

        invoice.ColourPages.Should().Be(0);
        invoice.BlackWhitePages.Should().Be(4);
        invoice.Total.Should().Be(8m);
        invoice.PrintedInBlackAndWhite.Should().BeTrue();
    }

    [Fact]
    public void TheInvoiceLayoutDrawsOnAPage()
    {
        var job = Job(totalPages: 200, map: new string('B', 150) + new string('C', 50), blackWhite: 2m, colour: 10m);
        foreach (var b in job.Batches) b.State = BatchState.Completed;
        job.StartedUtc = DateTimeOffset.UtcNow.AddHours(-5);
        job.FinishedUtc = DateTimeOffset.UtcNow;
        var invoice = Invoice.For(job, DateTimeOffset.UtcNow);

        // A4 at 100 dpi with one-inch margins: the same units a printer's MarginBounds uses.
        using var bitmap = new Bitmap(827, 1169);
        bitmap.SetResolution(100, 100);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.PageUnit = GraphicsUnit.Pixel;
            g.Clear(Color.White);
            InvoiceLayout.Draw(g, new RectangleF(100, 100, 627, 969), invoice, CultureInfo.GetCultureInfo("en-IN"));
        }

        if (Environment.GetEnvironmentVariable("PRINTPACER_INVOICE_PREVIEW") is { Length: > 0 } preview)
            bitmap.Save(preview);

        // Something was drawn, and nothing strayed into the margins.
        var inked = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
        for (var x = 0; x < bitmap.Width; x += 2)
        {
            if (bitmap.GetPixel(x, y).R >= 128) continue;
            inked++;
            x.Should().BeInRange(95, 732, "the invoice must stay inside the margins");
        }
        inked.Should().BeGreaterThan(500);
    }

    // ---- The engine ------------------------------------------------------------

    [Fact]
    public async Task PrintsTheInvoiceOnceAfterTheLastBatch()
    {
        await using var h = await EngineHarness.CreateAsync(10, colourPages: new[] { 3, 4, 9 });
        var job = await h.CreateJobWithInvoiceAsync(pagesPerBatch: 4, delayMinutes: 0, blackWhitePrice: 2m, colourPrice: 10m);

        h.Engine.StartJob(job.Id);

        (await h.WaitUntilAsync(() => h.Job.InvoiceState == InvoiceState.Printed, 20_000))
            .Should().BeTrue(h.Diagnostics());

        h.Job.State.Should().Be(JobState.Completed);
        h.InvoicePrinter.Printed.Should().ContainSingle();

        var invoice = h.InvoicePrinter.Printed[0];
        invoice.BlackWhitePages.Should().Be(7);
        invoice.ColourPages.Should().Be(3);
        invoice.Total.Should().Be(44m);
        invoice.ColourPageRanges.Should().Be("3–4, 9");
        h.Job.InvoicePrintedUtc.Should().NotBeNull();

        // The invoice is its own job, never part of a batch.
        h.PrintEngine.SubmissionCount.Should().Be(3);
        h.Printer.TotalPagesPrinted.Should().Be(10);
    }

    [Fact]
    public async Task NoInvoiceIsPrintedUnlessAskedFor()
    {
        await using var h = await EngineHarness.CreateAsync(4);
        var job = await h.CreateJobAsync(pagesPerBatch: 2, delayMinutes: 0);

        h.Engine.StartJob(job.Id);

        (await h.WaitForStateAsync(JobState.Completed, 20_000)).Should().BeTrue(h.Diagnostics());
        await Task.Delay(200);

        h.InvoicePrinter.Printed.Should().BeEmpty();
        h.Job.InvoiceState.Should().Be(InvoiceState.NotRequested);
        h.Job.Invoice.Should().BeNull();
    }

    [Fact]
    public async Task ABlackAndWhiteJobIsBilledAsBlackAndWhite()
    {
        await using var h = await EngineHarness.CreateAsync(4, colourPages: new[] { 1, 2 });
        var job = await h.CreateJobWithInvoiceAsync(4, 0, blackWhitePrice: 1m, colourPrice: 5m, ColourMode.BlackAndWhite);

        h.Engine.StartJob(job.Id);

        (await h.WaitUntilAsync(() => h.Job.InvoiceState == InvoiceState.Printed, 20_000))
            .Should().BeTrue(h.Diagnostics());
        h.InvoicePrinter.Printed[0].ColourPages.Should().Be(0);
        h.InvoicePrinter.Printed[0].Total.Should().Be(4m);
    }

    [Fact]
    public async Task ARefusedInvoiceCanBeSentAgain()
    {
        await using var h = await EngineHarness.CreateAsync(4);
        h.InvoicePrinter.Fail = true;
        var job = await h.CreateJobWithInvoiceAsync(4, 0, blackWhitePrice: 1m, colourPrice: 5m);

        h.Engine.StartJob(job.Id);

        (await h.WaitUntilAsync(() => h.Job.InvoiceState == InvoiceState.Failed, 20_000))
            .Should().BeTrue(h.Diagnostics());
        h.Job.State.Should().Be(JobState.Completed, "a bill that did not print does not un-print the pages");
        h.Notifier.Notifications.Should().Contain(n => n.Title == "Invoice not printed");

        h.InvoicePrinter.Fail = false;
        h.Engine.PrintInvoice(job.Id);

        (await h.WaitUntilAsync(() => h.Job.InvoiceState == InvoiceState.Printed)).Should().BeTrue(h.Diagnostics());
        h.InvoicePrinter.Printed.Should().ContainSingle();
    }

    [Fact]
    public async Task AnInvoiceInterruptedByACrashIsNotResentAutomatically()
    {
        await using var h = await EngineHarness.CreateAsync(4);
        h.InvoicePrinter.Hold = true;
        var job = await h.CreateJobWithInvoiceAsync(4, 0, blackWhitePrice: 1m, colourPrice: 5m);

        h.Engine.StartJob(job.Id);
        (await h.WaitUntilAsync(() => h.Job.InvoiceState == InvoiceState.Sending, 20_000))
            .Should().BeTrue(h.Diagnostics());

        await h.RestartApplicationAsync(graceful: false);
        h.InvoicePrinter.Hold = false;
        await Task.Delay(200);

        var recovered = (await h.JobsAsync()).Single();
        recovered.InvoiceState.Should().Be(InvoiceState.Interrupted);
        h.InvoicePrinter.Printed.Should().ContainSingle("it may already be in the tray");
    }

    [Fact]
    public async Task InvoiceSettingsSurviveARestart()
    {
        await using var h = await EngineHarness.CreateAsync(6, colourPages: new[] { 6 });
        await h.CreateJobWithInvoiceAsync(3, 60, blackWhitePrice: 1.25m, colourPrice: 7.5m);

        await h.RestartApplicationAsync();

        var job = (await h.JobsAsync()).Single();
        job.InvoiceState.Should().Be(InvoiceState.Waiting);
        job.Invoice!.BlackWhitePagePrice.Should().Be(1.25m);
        job.Invoice.ColourPagePrice.Should().Be(7.5m);
    }

    [Fact]
    public async Task RefusesAnInvoiceWithoutAColourForEveryPage()
    {
        await using var h = await EngineHarness.CreateAsync(4);

        var act = () => h.Engine.CreateJobAsync(h.Document, h.Printer.Name, 2, 0, StartMode.Immediately, null,
            300, ColourMode.Colour, new InvoiceRequest(1m, 5m, "BB"));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void AVersionOneDatabaseIsUpgradedInPlace()
    {
        var root = TempDirectory();
        var path = Path.Combine(root, "jobs.db");

        // Build a database, then strip it back to exactly what v1 wrote.
        using (var store = new JobStore(path))
        {
            store.InsertJob(Job(totalPages: 2, map: null, blackWhite: 0m, colour: 0m, invoice: false));
        }

        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE jobs DROP COLUMN invoice_state;
                ALTER TABLE jobs DROP COLUMN bw_page_price;
                ALTER TABLE jobs DROP COLUMN colour_page_price;
                ALTER TABLE jobs DROP COLUMN page_colour_map;
                ALTER TABLE jobs DROP COLUMN invoice_printed_utc;
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
                UPDATE schema_info SET version = 1;
                """;
            command.ExecuteNonQuery();
        }

        using (var store = new JobStore(path))
        {
            var job = store.LoadAllJobs().Single();
            job.InvoiceState.Should().Be(InvoiceState.NotRequested);

            job.InvoiceState = InvoiceState.Printed;
            store.UpdateJob(job);
        }

        using (var store = new JobStore(path))
        {
            store.LoadAllJobs().Single().InvoiceState.Should().Be(InvoiceState.Printed);
        }
    }

    // ---- Helpers -----------------------------------------------------------------

    private static PrintJob Job(int totalPages, string? map, decimal blackWhite, decimal colour,
        ColourMode colourMode = ColourMode.Colour, bool invoice = true)
    {
        var id = Guid.NewGuid().ToString("N");
        var job = new PrintJob
        {
            Id = id,
            SourcePath = "C:\\nowhere\\book.pdf",
            FileName = "book.pdf",
            FileSha256 = "00",
            TotalPages = totalPages,
            PrinterName = "Printer",
            PagesPerBatch = 5,
            ColourMode = colourMode,
            InvoiceState = invoice ? InvoiceState.Waiting : InvoiceState.NotRequested,
            BlackWhitePagePrice = blackWhite,
            ColourPagePrice = colour,
            PageColourMap = map,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        job.Batches.AddRange(PrintJob.PlanBatches(id, totalPages, 5));
        return job;
    }

    private static string TempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "printpacer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
