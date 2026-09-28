using System.Collections.Concurrent;
using PrintPacer.Core.Billing;
using PrintPacer.Core.Engine;
using PrintPacer.Core.Model;
using PrintPacer.Core.Pdf;
using PrintPacer.Core.Persistence;
using PrintPacer.Core.Simulation;

namespace PrintPacer.Tests;

/// <summary>
/// Wires a <see cref="JobEngine"/> to a fully simulated printer, spooler, device and clock,
/// in a throwaway data directory.
///
/// The engine runs its real loop against its real SQLite store; only the world outside the
/// application is simulated. That means these tests exercise the actual scheduling,
/// persistence, verification and recovery code, not a reimplementation of it.
/// </summary>
public sealed class EngineHarness : IAsyncDisposable
{
    public string Root { get; }
    public string PdfPath { get; }
    public PdfDocumentInfo Document { get; }

    public SimulatedPrinter Printer { get; }
    public SimulatedPrintEngine PrintEngine { get; private set; }
    public SimulatedDeviceProbe DeviceProbe { get; }
    public ManualClock Clock { get; }
    public RecordingNotifier Notifier { get; private set; }
    public RecordingPowerManager Power { get; private set; }
    public SimulatedInvoicePrinter InvoicePrinter { get; private set; }

    public JobStore Store { get; private set; }
    public JobEngine Engine { get; private set; }
    public AppSettings Settings { get; }

    public IReadOnlyList<RecoveryReport> LastRecovery { get; private set; } = Array.Empty<RecoveryReport>();

    private EngineHarness(string root, PdfDocumentInfo document, AppSettings settings)
    {
        Root = root;
        PdfPath = document.Path;
        Document = document;
        Settings = settings;

        Printer = new SimulatedPrinter("Simulated Printer");
        PrintEngine = new SimulatedPrintEngine(Printer);
        DeviceProbe = new SimulatedDeviceProbe(Printer);
        Clock = new ManualClock();
        Notifier = new RecordingNotifier();
        Power = new RecordingPowerManager();
        InvoicePrinter = new SimulatedInvoicePrinter();

        Store = new JobStore(Path.Combine(root, "jobs.db"));
        Engine = BuildEngine();
    }

    public static async Task<EngineHarness> CreateAsync(int pages, Action<AppSettings>? configure = null,
        IReadOnlyCollection<int>? colourPages = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "printpacer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var pdfPath = Path.Combine(root, $"fixture-{pages}.pdf");
        TestPdfBuilder.Write(pdfPath, pages, $"Test fixture {pages}", colourPages);

        var document = new PdfService().Load(pdfPath);

        var settings = new AppSettings
        {
            RetryIntervalMinutes = 5,
            MaxAutoRetries = 3,
            StallTimeoutMinutes = 30,
            // Poll the simulated queue on every tick. On its own this only narrows the gap
            // between two polls; WaitUntilAsync is what keeps the printer from crossing it.
            SpoolerPollSeconds = 0,
            BlockedGiveUpMinutes = 240,
            KeepAwakeWhilePrinting = true,
        };
        configure?.Invoke(settings);

        var harness = new EngineHarness(root, document, settings);
        await harness.StartEngineAsync();
        return harness;
    }

    private JobEngine BuildEngine() => new(
        Store,
        new SimulatedSpooler(Printer),
        PrintEngine,
        new SimulatedDeviceProbeFactory(DeviceProbe),
        Settings,
        Clock,
        Notifier,
        Power,
        // A fast tick keeps the tests quick without changing any engine behaviour, because
        // all scheduling decisions are driven by the injected clock rather than by ticks.
        TimeSpan.FromMilliseconds(5),
        InvoicePrinter);

    /// <summary>
    /// The most recent snapshot the engine published. Tests poll this rather than reaching
    /// into the engine's own job objects, which is both unsafe across threads and exactly
    /// what the production code is forbidden from doing.
    /// </summary>
    private volatile JobSnapshot? _latest;

    /// <summary>The latest snapshot of every job, for tests that run several jobs through the queue.</summary>
    private readonly ConcurrentDictionary<string, JobSnapshot> _byId = new();

    private async Task StartEngineAsync()
    {
        Engine.RecoveryCompleted += reports => LastRecovery = reports;
        Engine.JobChanged += snapshot =>
        {
            _latest = snapshot;
            _byId[snapshot.Id] = snapshot;
        };

        await Engine.StartAsync();

        // Recovery runs before the loop publishes anything, so seed from the store.
        var jobs = await Engine.GetJobsAsync();
        _byId.Clear();
        foreach (var job in jobs) _byId[job.Id] = job;
        _latest = jobs.LastOrDefault();
    }

    /// <summary>One job, as the engine last published it.</summary>
    public JobSnapshot Snapshot(string jobId) =>
        _byId.TryGetValue(jobId, out var snapshot)
            ? snapshot
            : throw new InvalidOperationException($"No snapshot of job {jobId} has been published.");

    /// <summary>Whether the job has been removed: gone from the engine, not merely unpublished.</summary>
    public async Task<bool> IsGoneAsync(string jobId) => (await Engine.GetJobsAsync()).All(j => j.Id != jobId);

    /// <summary>Writes another numbered test PDF next to the default one, as a second customer's file.</summary>
    public PdfDocumentInfo AddDocument(int pages, string name, IReadOnlyCollection<int>? colourPages = null)
    {
        var path = Path.Combine(Root, name);
        TestPdfBuilder.Write(path, pages, Path.GetFileNameWithoutExtension(name), colourPages);
        return new PdfService().Load(path);
    }

    /// <summary>The configuration a job gets from the defaults, pointed at the simulated printer.</summary>
    public JobConfiguration Config(int pagesPerBatch, int delayMinutes, ColourMode colourMode = ColourMode.Colour,
        PrintSettings? printSettings = null) => new()
    {
        PrinterName = Printer.Name,
        PagesPerBatch = pagesPerBatch,
        DelayMinutes = delayMinutes,
        ColourMode = colourMode,
        PrintSettings = printSettings ?? PrintSettings.Default,
    };

    public Task<JobSnapshot> CreateJobAsync(PdfDocumentInfo document, JobConfiguration configuration) =>
        Engine.CreateJobAsync(document, configuration);

    /// <summary>Waits for one job to reach a state, driving the printer meanwhile.</summary>
    public Task<bool> WaitForJobStateAsync(string jobId, JobState state, int timeoutMs = 10_000) =>
        WaitUntilAsync(() => _byId.TryGetValue(jobId, out var job) && job.State == state, timeoutMs);

    /// <summary>
    /// Runs the whole queue to the end, skipping every rest as it comes up. Returns false if it
    /// did not finish, or stopped for a decision.
    /// </summary>
    public async Task<bool> RunQueueToEndAsync(IReadOnlyCollection<string> jobIds, int timeoutMs = 60_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (jobIds.All(id => _byId.TryGetValue(id, out var j) && j.IsTerminal)) return true;

            var waiting = await WaitUntilAsync(() =>
                jobIds.All(id => _byId.TryGetValue(id, out var j) && j.IsTerminal) ||
                _byId.Values.Any(j => jobIds.Contains(j.Id) && j.State is JobState.CoolingDown or JobState.WaitingForStart) ||
                _byId.Values.Any(j => jobIds.Contains(j.Id) && j.State == JobState.Queued &&
                                      j.LastPrinterStatus?.Contains("Starts at") == true), 2_000);

            if (waiting) SkipDelay(24 * 60);
        }
        return jobIds.All(id => _byId.TryGetValue(id, out var j) && j.IsTerminal);
    }

    /// <summary>
    /// Simulates closing and reopening the application: the engine and its store are torn
    /// down and rebuilt from disk, exactly as they would be after a crash or a reboot.
    /// The simulated printer and its queue survive, as the real spooler would.
    /// </summary>
    public async Task RestartApplicationAsync(bool graceful = true)
    {
        if (graceful)
        {
            await Engine.DisposeAsync();
        }
        else
        {
            // An ungraceful exit: nothing is flushed, nothing is tidied. Only what the
            // engine already committed to SQLite survives.
            await Engine.SimulateAbruptTerminationAsync();
        }

        Store.Dispose();

        Store = new JobStore(Path.Combine(Root, "jobs.db"));
        PrintEngine = new SimulatedPrintEngine(Printer)
        {
            FailBeforeAnyOutput = PrintEngine.FailBeforeAnyOutput,
            FailAfterPartialOutput = PrintEngine.FailAfterPartialOutput,
            DriverIgnoresMonochrome = PrintEngine.DriverIgnoresMonochrome,
            CannotDuplex = PrintEngine.CannotDuplex,
            QueueShowsSingleSided = PrintEngine.QueueShowsSingleSided,
        };
        Notifier = new RecordingNotifier();
        Power = new RecordingPowerManager();
        Engine = BuildEngine();
        _latest = null;

        await StartEngineAsync();
    }

    /// <summary>The current job, as the engine last published it.</summary>
    public JobSnapshot Job =>
        _latest ?? Engine.GetJobsAsync().GetAwaiter().GetResult().LastOrDefault()
        ?? throw new InvalidOperationException("No job has been created yet.");

    public Task<IReadOnlyList<JobSnapshot>> JobsAsync() => Engine.GetJobsAsync();

    public Task<JobSnapshot> CreateJobAsync(int pagesPerBatch, int delayMinutes,
        StartMode startMode = StartMode.Immediately, DateTimeOffset? startAt = null,
        ColourMode colourMode = ColourMode.Colour, PrintSettings? printSettings = null) =>
        Engine.CreateJobAsync(Document, Printer.Name, pagesPerBatch, delayMinutes,
            startMode, startAt, 300, colourMode, printSettings: printSettings);

    /// <summary>A job that should print an invoice at the end, like ticking the box on the setup screen.</summary>
    public Task<JobSnapshot> CreateJobWithInvoiceAsync(int pagesPerBatch, int delayMinutes,
        decimal blackWhitePrice, decimal colourPrice, ColourMode colourMode = ColourMode.Colour,
        PrintSettings? printSettings = null)
    {
        var map = PageColourMap.Encode(PageColourAnalyzer.Analyse(PdfPath));
        return Engine.CreateJobAsync(Document, Printer.Name, pagesPerBatch, delayMinutes,
            StartMode.Immediately, null, 300, colourMode,
            new InvoiceRequest(blackWhitePrice, colourPrice, map), printSettings);
    }

    /// <summary>
    /// Waits for a condition while driving the simulated printer forward. Without the
    /// printer advancing, a queued job would never finish and every test would time out.
    ///
    /// The printer only takes a step once the engine has listed the queue since the last
    /// change, so it runs in lockstep with the monitor however late the engine's thread is
    /// scheduled. Otherwise a slow engine (cold JIT, a busy machine) can see a 50-page job at
    /// 40 pages and next find it gone - which, with no page counter to corroborate, the engine
    /// rightly refuses to call printed.
    /// </summary>
    public async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000,
        bool advancePrinter = true)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            if (advancePrinter && !Printer.HasUnobservedQueueChange) Printer.Advance();

            // Let a little simulated time pass on every poll. Without this the injected
            // clock would be frozen, and anything the engine schedules against it - the
            // page-counter settle window, stall detection - could never elapse. The step is
            // small enough that minute-scale delays still have to be skipped explicitly.
            Clock.Advance(TimeSpan.FromMilliseconds(100));

            await Task.Delay(5);
        }
        return condition();
    }

    public Task<bool> WaitForStateAsync(JobState state, int timeoutMs = 10_000, bool advancePrinter = true) =>
        WaitUntilAsync(() => _latest is { } snapshot && snapshot.State == state, timeoutMs, advancePrinter);

    public Task<bool> WaitForBatchStateAsync(int batchNumber, BatchState state, int timeoutMs = 10_000) =>
        WaitUntilAsync(() => Job.Batches.FirstOrDefault(b => b.Number == batchNumber)?.State == state, timeoutMs);

    /// <summary>Moves the clock past any pending delay so the next batch becomes due.</summary>
    public void SkipDelay(int minutes = 120) => Clock.Advance(TimeSpan.FromMinutes(minutes));

    public IReadOnlyList<JobEvent> Events(string? jobId = null) => Store.LoadEvents(jobId ?? Job.Id, 1000);

    /// <summary>Everything a failing assertion needs in order to be diagnosable.</summary>
    public string Diagnostics()
    {
        if (_latest is not { } job) return "(no job published yet)";

        var lines = new List<string>
        {
            "",
            $"job state       : {job.State}",
            $"attention       : {job.AttentionReason ?? "-"}",
            $"printer status  : {job.LastPrinterStatus ?? "-"}",
            $"current batch   : {job.CurrentBatchNumber} of {job.BatchCount}",
            $"submissions     : {PrintEngine.SubmissionCount}",
            $"printer pages   : {Printer.TotalPagesPrinted}",
            $"impression ctr  : {Printer.ImpressionCounter?.ToString() ?? "-"}",
            $"queue           : {string.Join(", ", Printer.Snapshot().Select(j => $"#{j.JobId} {j.Status} {j.PagesPrinted}/{j.TotalPages}"))}",
            "batches:",
        };

        foreach (var b in job.Batches)
        {
            lines.Add($"  {b.Number}: {b.RangeLabel,-16} {b.State,-14} confirmed={b.PagesConfirmedPrinted} " +
                      $"attempts={b.AttemptCount} note={b.Note ?? "-"}");
            if (b.LatestAttemptState is { } attemptState)
            {
                lines.Add($"      latest attempt: {attemptState} spoolPages={b.SpoolerPagesPrinted} " +
                          $"inQ={b.ObservedInQueue} delta={b.DeviceImpressionDelta?.ToString() ?? "-"}");
                lines.Add($"      evidence      : {b.LatestAttemptEvidence}");
                lines.Add($"      verdict       : {b.VerdictExplanation ?? "-"}");
            }
        }

        lines.Add("recent events:");
        foreach (var e in Events().TakeLast(15))
            lines.Add($"  [{e.Level}] {e.Category}: {e.Message} {e.Detail ?? ""}");

        return string.Join(Environment.NewLine, lines);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Engine.DisposeAsync(); } catch { /* teardown */ }
        try { Store.Dispose(); } catch { /* teardown */ }
        try { Directory.Delete(Root, recursive: true); } catch { /* teardown */ }
    }
}
