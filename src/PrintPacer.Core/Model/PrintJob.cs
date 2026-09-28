using PrintPacer.Core.Billing;
using PrintPacer.Core.Printing;

namespace PrintPacer.Core.Model;

/// <summary>
/// One document to be printed in paced batches. Persisted in its entirety; the
/// in-memory instance is only ever a cache of what is on disk.
/// </summary>
public sealed class PrintJob
{
    public required string Id { get; init; }

    /// <summary>
    /// "Job #12": a short number for people and logs, given when the job is created and never
    /// reused or changed. Not the queue position, which changes when jobs are reordered.
    /// </summary>
    public int Number { get; init; }

    /// <summary>Place in the print queue; lower goes first. Stored, so the chosen order survives a restart.</summary>
    public int QueuePosition { get; set; }

    // ---- Document identity -------------------------------------------------
    public required string SourcePath { get; set; }
    public required string FileName { get; init; }
    public long FileSizeBytes { get; init; }

    /// <summary>
    /// SHA-256 of the source file, captured at load time. On resume we re-hash and
    /// refuse to continue silently if the file changed underneath us: page N of a
    /// different document is not page N.
    /// </summary>
    public required string FileSha256 { get; init; }

    public int TotalPages { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Producer { get; init; }
    public string? PdfVersion { get; init; }
    public bool IsEncrypted { get; init; }

    // ---- Configuration -----------------------------------------------------
    public required string PrinterName { get; set; }

    /// <summary>
    /// Print units per batch (see <see cref="PrintPlan.Unit"/>): pages for a plain single-sided
    /// job, as it has always been, and physical sheets for everything else. The database column
    /// keeps its old name.
    /// </summary>
    public int PagesPerBatch { get; init; }
    public int DelayMinutes { get; init; }
    public StartMode StartMode { get; init; }
    public DateTimeOffset? ScheduledStartUtc { get; set; }

    /// <summary>Render resolution handed to PDFium before drawing onto the printer DC.</summary>
    public int RenderDpi { get; init; } = 300;

    /// <summary>Applies to every batch of the job, including retries and split remainders.</summary>
    public ColourMode ColourMode { get; init; } = ColourMode.Colour;
    public bool FitToPrintableArea { get; init; } = true;
    public bool AutoRotate { get; init; } = true;

    /// <summary>
    /// Page selection, layout and sides. Jobs stored before these existed read back as the
    /// defaults - all pages, normal layout, single-sided - which is exactly how they printed.
    /// </summary>
    public PrintSettings PrintSettings { get; init; } = PrintSettings.Default;

    private PrintPlan? _plan;

    /// <summary>
    /// Which sheet carries which pages, derived from <see cref="PrintSettings"/> and the page
    /// count. Batches are ranges of this plan's units.
    /// </summary>
    public PrintPlan Plan => _plan ??= PrintPlan.Build(PrintSettings, TotalPages);

    /// <summary>
    /// The plan, or null when the stored settings cannot make one (only possible for a damaged
    /// database row). For displays and reports, which must keep working; printing uses
    /// <see cref="Plan"/> and refuses such a job outright.
    /// </summary>
    public PrintPlan? TryGetPlan()
    {
        try { return Plan; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Gives every batch the plan its unit numbers refer to. Called when a job is created or loaded.</summary>
    public void AttachPlanToBatches()
    {
        foreach (var batch in Batches) batch.Plan = Plan;
    }

    // ---- Invoice -----------------------------------------------------------
    public InvoiceState InvoiceState { get; set; } = InvoiceState.NotRequested;
    public decimal BlackWhitePagePrice { get; init; }
    public decimal ColourPagePrice { get; init; }

    /// <summary>
    /// Which pages carry colour, one character per page (see <see cref="Billing.PageColourMap"/>).
    /// Captured at setup so the bill does not depend on the source file still being there.
    /// </summary>
    public string? PageColourMap { get; init; }

    public DateTimeOffset? InvoicePrintedUtc { get; set; }

    public bool WantsInvoice => InvoiceState != InvoiceState.NotRequested;

    // ---- Runtime -----------------------------------------------------------
    public JobState State { get; set; } = JobState.Draft;

    /// <summary>1-based index of the batch currently in flight or next to run.</summary>
    public int CurrentBatchNumber { get; set; } = 1;

    /// <summary>
    /// Absolute UTC instant at which the next batch may be submitted. Stored as an
    /// absolute time, never as a countdown, so sleep/hibernate/restart cannot lose it.
    /// </summary>
    public DateTimeOffset? NextBatchDueUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }

    public string? LastError { get; set; }
    public string? AttentionReason { get; set; }
    public string? LastPrinterStatus { get; set; }

    /// <summary>Consecutive blocked-state polls, used to pace retry logging and notifications.</summary>
    public int ConsecutiveBlockedChecks { get; set; }

    public List<Batch> Batches { get; init; } = new();

    // ---- Derived -----------------------------------------------------------
    public int BatchCount => Batches.Count;

    /// <summary>Print units finished or accepted, in the unit the batches count.</summary>
    public int PagesAccountedFor => Batches
        .Where(b => b.State is BatchState.Completed or BatchState.Skipped)
        .Sum(b => b.PageCount);

    public Batch? CurrentBatch => Batches.FirstOrDefault(b => b.Number == CurrentBatchNumber);

    /// <summary>
    /// The next batch to send, in document order.
    ///
    /// Ordered by first page rather than by batch number on purpose. When a partly printed
    /// batch is split, the remainder is added with a new, higher number, so ordering by
    /// number would send it after every later batch and stack the output tray out of
    /// sequence. Page order is what the reader actually cares about.
    /// </summary>
    public Batch? NextPendingBatch => Batches
        .Where(b => b.State == BatchState.Pending)
        .OrderBy(b => b.FirstPage)
        .FirstOrDefault();

    public bool IsTerminal => State is JobState.Completed or JobState.Failed or JobState.Cancelled;

    public bool IsActive => !IsTerminal && State != JobState.Draft;

    /// <summary>Whether anything of this job has ever been handed to the printer.</summary>
    public bool HasSentAnything => Batches.Any(b => b.Attempts.Count > 0);

    /// <summary>
    /// Not yet started and nothing sent: the job may still be edited, reordered or removed
    /// without any record of printing being touched.
    /// </summary>
    public bool IsWaiting => State is JobState.Draft or JobState.Queued && !HasSentAnything;

    public string Label => Number > 0 ? $"Job #{Number}" : "Job";

    /// <summary>The job's settings as one value, for display, editing and copying.</summary>
    public JobConfiguration Configuration => new()
    {
        PrinterName = PrinterName,
        PagesPerBatch = PagesPerBatch,
        DelayMinutes = DelayMinutes,
        StartMode = StartMode,
        ScheduledStartUtc = ScheduledStartUtc,
        RenderDpi = RenderDpi,
        ColourMode = ColourMode,
        PrintSettings = PrintSettings,
        Invoice = WantsInvoice ? new InvoiceRequest(BlackWhitePagePrice, ColourPagePrice, PageColourMap) : null,
    };

    /// <summary>
    /// A job for a document, configured and batched but not started. The configuration is copied
    /// in field by field; the caller's value is never kept, so nothing else can change this job.
    /// </summary>
    public static PrintJob Create(string id, int number, int queuePosition, DocumentIdentity document,
        JobConfiguration configuration, DateTimeOffset now)
    {
        var print = configuration.PrintSettings;
        var plan = PrintPlan.Build(print, document.TotalPages);
        var invoice = configuration.Invoice;

        var job = new PrintJob
        {
            Id = id,
            Number = number,
            QueuePosition = queuePosition,
            SourcePath = document.SourcePath,
            FileName = document.FileName,
            FileSizeBytes = document.FileSizeBytes,
            FileSha256 = document.FileSha256,
            TotalPages = document.TotalPages,
            Title = document.Title,
            Author = document.Author,
            Producer = document.Producer,
            PdfVersion = document.PdfVersion,
            IsEncrypted = document.IsEncrypted,
            PrinterName = configuration.PrinterName,
            PagesPerBatch = Math.Clamp(configuration.PagesPerBatch, 1, Math.Max(1, plan.UnitCount)),
            DelayMinutes = Math.Clamp(configuration.DelayMinutes, 0, JobConfiguration.MaxDelayMinutes),
            StartMode = configuration.StartMode,
            ScheduledStartUtc = configuration.StartMode == StartMode.AtTime ? configuration.ScheduledStartUtc : null,
            RenderDpi = configuration.RenderDpi,
            ColourMode = configuration.ColourMode,
            PrintSettings = print,
            InvoiceState = invoice is null ? InvoiceState.NotRequested : InvoiceState.Waiting,
            BlackWhitePagePrice = invoice?.BlackWhitePagePrice ?? 0m,
            ColourPagePrice = invoice?.ColourPagePrice ?? 0m,
            PageColourMap = invoice?.PageColourMap,
            CreatedUtc = now,
            UpdatedUtc = now,
            State = JobState.Draft,
        };

        // Batching comes last: the plan has already selected and arranged the pages, so the
        // batches divide finished sheets and cannot break a spread or a sheet's two sides.
        job.Batches.AddRange(PlanBatches(id, job.Plan, job.PagesPerBatch));
        return job;
    }

    /// <summary>
    /// The same job - same identity, document, number, place and state - with a different
    /// configuration and batches planned afresh from it. Only for a job that has sent nothing.
    /// </summary>
    public PrintJob WithConfiguration(JobConfiguration configuration, DateTimeOffset now)
    {
        if (!IsWaiting) throw new InvalidOperationException("Only a job that has not started can be changed.");

        var job = Create(Id, Number, QueuePosition, DocumentIdentity.Of(this), configuration, CreatedUtc);
        job.State = State;
        job.UpdatedUtc = now;
        return job;
    }

    /// <summary>
    /// Computes the batch layout for a document. The final batch absorbs the remainder,
    /// so a 200-page document at 50/batch yields 4 batches, and a 201-page document
    /// yields 5 (the last containing a single page).
    /// </summary>
    public static List<Batch> PlanBatches(string jobId, int totalPages, int pagesPerBatch)
        => PlanBatches(jobId, totalPages, pagesPerBatch, null);

    /// <summary>
    /// Divides a job's plan into batches of whole units. Page selection and imposition have
    /// already happened by now, so for a booklet or brochure the units are sheets, and a batch
    /// always holds both sides of every sheet in it.
    /// </summary>
    public static List<Batch> PlanBatches(string jobId, PrintPlan plan, int unitsPerBatch)
        => PlanBatches(jobId, plan.UnitCount, unitsPerBatch, plan);

    private static List<Batch> PlanBatches(string jobId, int totalPages, int pagesPerBatch, PrintPlan? plan)
    {
        if (totalPages <= 0) throw new ArgumentOutOfRangeException(nameof(totalPages));
        if (pagesPerBatch <= 0) throw new ArgumentOutOfRangeException(nameof(pagesPerBatch));

        var batches = new List<Batch>();
        var number = 1;
        for (var first = 1; first <= totalPages; first += pagesPerBatch)
        {
            var last = Math.Min(first + pagesPerBatch - 1, totalPages);
            batches.Add(new Batch
            {
                JobId = jobId,
                Number = number++,
                FirstPage = first,
                LastPage = last,
                State = BatchState.Pending,
                Plan = plan,
            });
        }
        return batches;
    }
}

/// <summary>What a job knows about its document, taken when the PDF was added.</summary>
public sealed record DocumentIdentity(
    string SourcePath,
    string FileName,
    long FileSizeBytes,
    string FileSha256,
    int TotalPages,
    string? Title = null,
    string? Author = null,
    string? Producer = null,
    string? PdfVersion = null,
    bool IsEncrypted = false)
{
    public static DocumentIdentity Of(PrintJob job) => new(job.SourcePath, job.FileName, job.FileSizeBytes,
        job.FileSha256, job.TotalPages, job.Title, job.Author, job.Producer, job.PdfVersion, job.IsEncrypted);
}
