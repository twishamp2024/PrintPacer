using FluentAssertions;
using Microsoft.Data.Sqlite;
using PrintPacer.Core.Model;
using PrintPacer.Core.Persistence;
using PrintPacer.Core.Printing;
using Xunit;

namespace PrintPacer.Tests;

/// <summary>
/// The print mode: colour by default, and a black and white job asks for black and white on
/// every batch it sends - across rests, restarts and old data - without ever losing the job
/// when a driver will not cooperate.
/// </summary>
public class ColourModeTests
{
    // ---- Defaults ------------------------------------------------------------------

    [Fact]
    public async Task ColourIsTheDefaultAndEveryBatchAsksForColour()
    {
        new AppSettings().ColourMode.Should().Be(ColourMode.Colour);

        await using var h = await EngineHarness.CreateAsync(20);
        var job = await h.CreateJobAsync(pagesPerBatch: 10, delayMinutes: 0);
        job.ColourMode.Should().Be(ColourMode.Colour);

        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.Completed)).Should().BeTrue(h.Diagnostics());

        h.PrintEngine.SubmittedRequests.Should().HaveCount(2)
            .And.OnlyContain(r => r.ColourMode == ColourMode.Colour);
        h.Events().Should().NotContain(e => e.Message.Contains("black & white"),
            "a colour job has nothing to say about black and white");
    }

    // ---- The whole lifecycle ---------------------------------------------------------

    [Fact]
    public async Task EveryBatchOfABlackAndWhiteJobIsSentAsBlackAndWhite()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        var job = await h.CreateJobAsync(pagesPerBatch: 10, delayMinutes: 30, colourMode: ColourMode.BlackAndWhite);
        job.ColourMode.Should().Be(ColourMode.BlackAndWhite);
        job.BatchCount.Should().Be(10);

        h.Engine.StartJob(job.Id);
        // Rest between every batch, as the real thing does.
        for (var i = 0; i < 30 && h.Job.State != JobState.Completed; i++)
        {
            h.SkipDelay(31);
            await h.WaitUntilAsync(() => h.Job.State == JobState.Completed, 1_500);
        }

        h.Job.State.Should().Be(JobState.Completed, h.Diagnostics());
        h.PrintEngine.SubmittedRequests.Should().HaveCount(10)
            .And.OnlyContain(r => r.ColourMode == ColourMode.BlackAndWhite,
                "the mode belongs to the job, not to its first batch");
        h.PrintEngine.SubmittedRequests.Select(r => r.FirstPage)
            .Should().Equal(1, 11, 21, 31, 41, 51, 61, 71, 81, 91);

        // The queue reported each job's own colour setting, and the engine logged the proof.
        h.Events().Count(e => e.Message.StartsWith("Windows confirms batch") && e.Message.EndsWith("black & white."))
            .Should().Be(10, h.Diagnostics());
        h.Printer.TotalPagesPrinted.Should().Be(100);
    }

    [Fact]
    public async Task ABlackAndWhiteJobStaysBlackAndWhiteAcrossARestart()
    {
        await using var h = await EngineHarness.CreateAsync(150);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 60, colourMode: ColourMode.BlackAndWhite);

        h.Engine.StartJob(job.Id);
        (await h.WaitForBatchStateAsync(1, BatchState.Completed)).Should().BeTrue(h.Diagnostics());
        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue();
        var beforeRestart = h.PrintEngine.SubmittedRequests.ToList();

        await h.RestartApplicationAsync();

        h.Job.ColourMode.Should().Be(ColourMode.BlackAndWhite, "the mode is persisted with the job");

        for (var i = 0; i < 4 && h.Job.State != JobState.Completed; i++)
        {
            h.SkipDelay(61);
            await h.WaitUntilAsync(() => h.Job.State == JobState.Completed, 8_000);
        }

        h.Job.State.Should().Be(JobState.Completed, h.Diagnostics());
        beforeRestart.Concat(h.PrintEngine.SubmittedRequests).Should().HaveCount(3)
            .And.OnlyContain(r => r.ColourMode == ColourMode.BlackAndWhite);
    }

    [Fact]
    public async Task TheReportAndTheJobLogSayWhichModeTheJobPrintsIn()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var job = await h.CreateJobAsync(pagesPerBatch: 5, delayMinutes: 0, colourMode: ColourMode.BlackAndWhite);

        (await h.Engine.BuildReportAsync(job.Id)).Should().Contain("Print mode    : Black & white");
        h.Events().Should().Contain(e => e.Message.StartsWith("Job created") && e.Detail!.Contains("black & white"));
    }

    // ---- A driver that will not cooperate --------------------------------------------

    [Fact]
    public async Task ADriverWithoutABlackAndWhiteSettingIsReportedAndTheJobStillFinishes()
    {
        await using var h = await EngineHarness.CreateAsync(20);
        h.PrintEngine.DriverIgnoresMonochrome = true;
        var job = await h.CreateJobAsync(pagesPerBatch: 10, delayMinutes: 0, colourMode: ColourMode.BlackAndWhite);

        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.Completed)).Should().BeTrue(h.Diagnostics());

        h.Printer.TotalPagesPrinted.Should().Be(20, "the pages are still sent, in greyscale");
        var warnings = h.Events().Where(e => e.Level == "Warning" && e.Message.Contains("black & white")).ToList();
        warnings.Should().HaveCount(2, "each batch says so rather than claiming black and white");
        warnings.Should().OnlyContain(w => w.Detail!.Contains("does not offer a black & white setting"));
        h.Events().Should().NotContain(e => e.Message.StartsWith("Windows confirms"));
    }

    [Fact]
    public void OnlyAnHonouredRequestOrAMonochromePrinterCountsAsGuaranteed()
    {
        new MonochromeCheck(MonochromeSupport.Honoured).Guaranteed.Should().BeTrue();
        new MonochromeCheck(MonochromeSupport.MonochromeDevice).Guaranteed.Should().BeTrue();
        new MonochromeCheck(MonochromeSupport.NotExposed).Guaranteed.Should().BeFalse();
        new MonochromeCheck(MonochromeSupport.Unknown).Guaranteed.Should().BeFalse();

        new MonochromeCheck(MonochromeSupport.Honoured).Warning("P").Should().BeNull();
        new MonochromeCheck(MonochromeSupport.NotExposed).Warning("P").Should().Contain("\"P\"");
        new MonochromeCheck(MonochromeSupport.Unknown, "driver crashed").Warning("P").Should().Contain("driver crashed");
    }

    [Fact]
    public void AMissingPrinterIsUnknownRatherThanAnError()
    {
        PrinterColour.Check($"No such printer {Guid.NewGuid():N}").Support.Should().Be(MonochromeSupport.Unknown);
    }

    // ---- Persistence -------------------------------------------------------------------

    [Fact]
    public void TheModeIsStoredWithTheJobAndOldValuesReadSafely()
    {
        var path = Path.Combine(TempDirectory(), "jobs.db");

        using (var store = new JobStore(path))
        {
            store.InsertJob(Job("bw", ColourMode.BlackAndWhite));
            store.InsertJob(Job("colour", ColourMode.Colour));
            store.InsertJob(Job("odd", ColourMode.Colour));
        }

        // 1.1 wrote the same column as a 0/1 greyscale flag, so its jobs read back as they
        // were set up. A value no version ever wrote falls back to colour, the default.
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET grayscale = 7 WHERE id = 'odd';";
            command.ExecuteNonQuery();
        }

        using (var store = new JobStore(path))
        {
            var jobs = store.LoadAllJobs().ToDictionary(j => j.Id);
            jobs["bw"].ColourMode.Should().Be(ColourMode.BlackAndWhite);
            jobs["colour"].ColourMode.Should().Be(ColourMode.Colour);
            jobs["odd"].ColourMode.Should().Be(ColourMode.Colour);
        }
    }

    [Fact]
    public void TheLastChosenModeIsRememberedAsANameAndDefaultsToColour()
    {
        var path = Path.Combine(TempDirectory(), "settings.json");
        var store = new SettingsStore(path);

        store.Load().ColourMode.Should().Be(ColourMode.Colour, "no settings file yet");

        store.Save(new AppSettings { ColourMode = ColourMode.BlackAndWhite });
        File.ReadAllText(path).Should().Contain("\"colourMode\": \"BlackAndWhite\"").And.NotContain("grayscale");
        store.Load().ColourMode.Should().Be(ColourMode.BlackAndWhite);

        File.WriteAllText(path, """{ "colourMode": 5 }""");
        store.Load().ColourMode.Should().Be(ColourMode.Colour, "an unknown value falls back to colour");
    }

    [Theory]
    [InlineData("""{ "grayscale": true }""", ColourMode.BlackAndWhite)]
    [InlineData("""{ "grayscale": false }""", ColourMode.Colour)]
    [InlineData("""{ "defaultPagesPerBatch": 25 }""", ColourMode.Colour)]
    public void ASettingsFileFromVersionOnePointOneKeepsItsGreyscaleChoice(string json, ColourMode expected)
    {
        var path = Path.Combine(TempDirectory(), "settings.json");
        File.WriteAllText(path, json);
        var store = new SettingsStore(path);

        var settings = store.Load();
        settings.ColourMode.Should().Be(expected);

        // Migrated once: the old key is not written back.
        store.Save(settings);
        File.ReadAllText(path).Should().NotContain("grayscale");
        store.Load().ColourMode.Should().Be(expected);
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static PrintJob Job(string id, ColourMode mode)
    {
        var job = new PrintJob
        {
            Id = id,
            SourcePath = "C:\\nowhere\\book.pdf",
            FileName = "book.pdf",
            FileSha256 = "00",
            TotalPages = 10,
            PrinterName = "Printer",
            PagesPerBatch = 5,
            ColourMode = mode,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        job.Batches.AddRange(PrintJob.PlanBatches(id, 10, 5));
        return job;
    }

    private static string TempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "printpacer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
