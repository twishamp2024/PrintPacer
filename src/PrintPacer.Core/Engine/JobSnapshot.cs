using PrintPacer.Core.Billing;
using PrintPacer.Core.Model;

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

    public int PageCount => LastPage - FirstPage + 1;

    public int PagesConfirmedPrinted { get; init; }
    public string? Note { get; init; }

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
            PagesConfirmedPrinted = batch.PagesConfirmedPrinted,
            Note = batch.Note,
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
    public required string FileName { get; init; }
    public required string SourcePath { get; init; }
    public required string PrinterName { get; init; }

    public required int TotalPages { get; init; }
    public required int PagesPerBatch { get; init; }
    public required int DelayMinutes { get; init; }

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

    public BatchSnapshot? CurrentBatch => Batches.FirstOrDefault(b => b.Number == CurrentBatchNumber);

    public BatchSnapshot? NextPendingBatch => Batches
        .Where(b => b.State == BatchState.Pending)
        .OrderBy(b => b.FirstPage)
        .FirstOrDefault();

    public int PendingBatchCount => Batches.Count(b => b.State == BatchState.Pending);

    /// <summary>
    /// Pages we believe have physically printed: whole batches that finished, plus any
    /// partial count recorded for a batch that stopped part-way.
    /// </summary>
    public int PagesDone => Batches.Sum(b =>
        b.State is BatchState.Completed or BatchState.Skipped ? b.PageCount : b.PagesConfirmedPrinted);

    public static JobSnapshot From(PrintJob job) => new()
    {
        Id = job.Id,
        FileName = job.FileName,
        SourcePath = job.SourcePath,
        PrinterName = job.PrinterName,
        TotalPages = job.TotalPages,
        PagesPerBatch = job.PagesPerBatch,
        DelayMinutes = job.DelayMinutes,
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
