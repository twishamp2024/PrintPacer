using PrintPacer.Core.Billing;

namespace PrintPacer.Core.Model;

/// <summary>
/// One document to be printed in paced batches. Persisted in its entirety; the
/// in-memory instance is only ever a cache of what is on disk.
/// </summary>
public sealed class PrintJob
{
    public required string Id { get; init; }

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
    public int PagesPerBatch { get; init; }
    public int DelayMinutes { get; init; }
    public StartMode StartMode { get; init; }
    public DateTimeOffset? ScheduledStartUtc { get; set; }

    /// <summary>Render resolution handed to PDFium before drawing onto the printer DC.</summary>
    public int RenderDpi { get; init; } = 300;

    public bool Grayscale { get; init; }
    public bool FitToPrintableArea { get; init; } = true;
    public bool AutoRotate { get; init; } = true;

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

    /// <summary>
    /// Computes the batch layout for a document. The final batch absorbs the remainder,
    /// so a 200-page document at 50/batch yields 4 batches, and a 201-page document
    /// yields 5 (the last containing a single page).
    /// </summary>
    public static List<Batch> PlanBatches(string jobId, int totalPages, int pagesPerBatch)
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
            });
        }
        return batches;
    }
}
