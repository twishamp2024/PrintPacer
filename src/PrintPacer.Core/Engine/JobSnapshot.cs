using PrintPacer.Core.Billing;
using PrintPacer.Core.Model;
using PrintPacer.Core.Printing;

namespace PrintPacer.Core.Engine;

/// <summary>
/// An immutable picture of one batch, taken on the engine loop.
/// </summary>
public sealed record BatchSnapshot
{
    public required int Number { get; init; }
    public required int FirstPage { get; init; }
    public required int LastPage { get; init; }
    public required BatchState State { get; init; }
    public required string RangeLabel { get; init; }

    /// <summary>Print units in the batch: pages for a plain job, sheets otherwise.</summary>
    public int PageCount => LastPage - FirstPage + 1;

    /// <summary>Printed sides in the batch: what Windows counts as its pages.</summary>
    public int Impressions { get; init; }

    public int PagesConfirmedPrinted { get; init; }
    public string? Note { get; init; }

    public DateTimeOffset? SubmittedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; init; }

    public int AttemptCount { get; init; }
    public int SpoolerPagesPrinted { get; init; }
    public string? LastSpoolerStatus { get; init; }
    public string? VerdictExplanation { get; init; }

    /// <summary>State of the most recent submission, if there has been one.</summary>
    public AttemptState? LatestAttemptState { get; init; }

    /// <summary>What was actually observed about the most recent submission.</summary>
    public CompletionEvidence LatestAttemptEvidence { get; init; }

    /// <summary>Change in the printer's physical page counter across the last submission.</summary>
    public long? DeviceImpressionDelta { get; init; }

    /// <summary>Whether the last submission was ever actually seen in the Windows queue.</summary>
    public bool ObservedInQueue { get; init; }

    public static BatchSnapshot From(Batch batch)
    {
        var attempt = batch.LatestAttempt;
        return new BatchSnapshot
        {
            LatestAttemptState = attempt?.State,
            LatestAttemptEvidence = attempt?.Evidence ?? CompletionEvidence.None,
            DeviceImpressionDelta = attempt?.DeviceImpressionDelta,
            ObservedInQueue = attempt?.ObservedInQueue ?? false,
            Number = batch.Number,
            FirstPage = batch.FirstPage,
            LastPage = batch.LastPage,
            State = batch.State,
            RangeLabel = batch.RangeLabel,
            Impressions = batch.Impressions,
            PagesConfirmedPrinted = batch.PagesConfirmedPrinted,
            Note = batch.Note,
            SubmittedUtc = batch.SubmittedUtc,
            FinishedUtc = batch.FinishedUtc,
            AttemptCount = batch.Attempts.Count,
            SpoolerPagesPrinted = attempt?.SpoolerPagesPrinted ?? 0,
            LastSpoolerStatus = attempt?.LastSpoolerStatus,
            VerdictExplanation = attempt?.VerdictExplanation,
        };
    }
}

/// <summary>
/// An immutable picture of one job, taken on the engine loop and handed to the user
/// interface.
///
/// The engine's own <see cref="PrintJob"/> objects are mutable and are only ever touched
/// from the engine loop. Passing one to the UI thread would mean the interface reading a
/// list of batches while the engine appends to it - a race that shows up as an occasional
/// blank screen or a hard-to-reproduce crash in the middle of an overnight run. Handing
/// over a snapshot removes the possibility entirely.
/// </summary>
public sealed record JobSnapshot
{
    public required string Id { get; init; }

    /// <summary>"Job #12": stable for the life of the job.</summary>
    public int Number { get; init; }

    /// <summary>Place in the print queue; lower goes first.</summary>
    public int QueuePosition { get; init; }

    public string Label => Number > 0 ? $"Job #{Number}" : "Job";

    public required string FileName { get; init; }
    public required string SourcePath { get; init; }
    public required string PrinterName { get; init; }

    public required int TotalPages { get; init; }
    public required int PagesPerBatch { get; init; }
    public required int DelayMinutes { get; init; }
    public ColourMode ColourMode { get; init; }

    /// <summary>Page selection, layout and sides the job prints with.</summary>
    public PrintSettings PrintSettings { get; init; } = PrintSettings.Default;

    /// <summary>What batches count: pages for a plain job, physical sheets otherwise.</summary>
    public PrintUnit Unit { get; init; } = PrintUnit.Page;

    /// <summary>Print units in the whole job.</summary>
    public int UnitCount { get; init; }

    /// <summary>Source pages that print after page selection.</summary>
    public int SelectedPageCount { get; init; }

    /// <summary>"50 of 100 pages (odd pages) on 25 sheets", from the job's plan.</summary>
    public string SizeSummary { get; init; } = "";

    public string UnitName(int count) => Unit == PrintUnit.Page
        ? (count == 1 ? "page" : "pages")
        : (count == 1 ? "sheet" : "sheets");

    public required JobState State { get; init; }
    public required int CurrentBatchNumber { get; init; }

    public DateTimeOffset? NextBatchDueUtc { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; init; }

    public string? AttentionReason { get; init; }
    public string? LastPrinterStatus { get; init; }
    public string? LastError { get; init; }

    public string? Title { get; init; }
    public string FileSha256 { get; init; } = "";

    public InvoiceState InvoiceState { get; init; }
    public DateTimeOffset? InvoicePrintedUtc { get; init; }

    /// <summary>
    /// The bill as it stands: pages printed so far, charged at the job's rates. Null when the
    /// user did not ask for an invoice.
    /// </summary>
    public Invoice? Invoice { get; init; }

    public bool WantsInvoice => InvoiceState != InvoiceState.NotRequested;

    public required IReadOnlyList<BatchSnapshot> Batches { get; init; }

    public int BatchCount => Batches.Count;

    public bool IsTerminal => State is JobState.Completed or JobState.Failed or JobState.Cancelled;
    public bool IsActive => !IsTerminal && State != JobState.Draft;

    /// <summary>Whether any batch of this job has ever been handed to the printer.</summary>
    public bool HasSentAnything { get; init; }

    /// <summary>Not started and nothing sent: still editable, reorderable and removable.</summary>
    public bool IsWaiting => State is JobState.Draft or JobState.Queued && !HasSentAnything;

    /// <summary>The job's own settings, exactly as stored with it.</summary>
    public required JobConfiguration Configuration { get; init; }

    /// <summary>The document as it was when the job was added.</summary>
    public required DocumentIdentity Document { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public BatchSnapshot? CurrentBatch => Batches.FirstOrDefault(b => b.Number == CurrentBatchNumber);

    public BatchSnapshot? NextPendingBatch => Batches
        .Where(b => b.State == BatchState.Pending)
        .OrderBy(b => b.FirstPage)
        .FirstOrDefault();

    public int PendingBatchCount => Batches.Count(b => b.State == BatchState.Pending);

    /// <summary>
    /// Units (pages, or sheets for a sheet-based job) we believe have physically printed: whole
    /// batches that finished, plus any partial count recorded for a batch that stopped part-way.
    /// </summary>
    public int PagesDone => Batches.Sum(b =>
        b.State is BatchState.Completed or BatchState.Skipped ? b.PageCount : b.PagesConfirmedPrinted);

    public static JobSnapshot From(PrintJob job)
    {
        var plan = job.TryGetPlan();
        return From(job, plan);
    }

    private static JobSnapshot From(PrintJob job, PrintPlan? plan) => new()
    {
        Id = job.Id,
        Number = job.Number,
        QueuePosition = job.QueuePosition,
        HasSentAnything = job.HasSentAnything,
        Configuration = job.Configuration,
        Document = DocumentIdentity.Of(job),
        CreatedUtc = job.CreatedUtc,
        FileName = job.FileName,
        SourcePath = job.SourcePath,
        PrinterName = job.PrinterName,
        TotalPages = job.TotalPages,
        PagesPerBatch = job.PagesPerBatch,
        DelayMinutes = job.DelayMinutes,
        ColourMode = job.ColourMode,
        PrintSettings = job.PrintSettings,
        Unit = plan?.Unit ?? PrintUnit.Page,
        UnitCount = plan?.UnitCount ?? job.TotalPages,
        SelectedPageCount = plan?.SelectedPages.Count ?? job.TotalPages,
        SizeSummary = plan?.DescribeSize() ?? $"{job.TotalPages} pages (print settings unreadable)",
        State = job.State,
        CurrentBatchNumber = job.CurrentBatchNumber,
        NextBatchDueUtc = job.NextBatchDueUtc,
        StartedUtc = job.StartedUtc,
        FinishedUtc = job.FinishedUtc,
        AttentionReason = job.AttentionReason,
        LastPrinterStatus = job.LastPrinterStatus,
        LastError = job.LastError,
        Title = job.Title,
        FileSha256 = job.FileSha256,
        InvoiceState = job.InvoiceState,
        InvoicePrintedUtc = job.InvoicePrintedUtc,
        Invoice = job.WantsInvoice ? Invoice.For(job, job.FinishedUtc ?? DateTimeOffset.UtcNow) : null,
        Batches = job.Batches.OrderBy(b => b.FirstPage).Select(BatchSnapshot.From).ToList(),
    };
}
