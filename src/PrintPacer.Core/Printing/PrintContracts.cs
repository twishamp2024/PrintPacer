namespace PrintPacer.Core.Printing;

/// <summary>A request to print one contiguous page range as exactly one Windows print job.</summary>
public sealed record PrintRequest
{
    public required string SourcePath { get; init; }

    /// <summary>1-based inclusive page range within the source document.</summary>
    public required int FirstPage { get; init; }
    public required int LastPage { get; init; }

    public required string PrinterName { get; init; }

    /// <summary>
    /// Document name handed to the spooler. Must be globally unique per attempt: it is the
    /// only reliable way to recognise our own job in the queue after a crash or restart.
    /// </summary>
    public required string DocumentToken { get; init; }

    public int RenderDpi { get; init; } = 300;
    public bool Grayscale { get; init; }
    public bool FitToPrintableArea { get; init; } = true;
    public bool AutoRotate { get; init; } = true;

    /// <summary>
    /// Deliberately fixed at one. Multiple copies would multiply any duplicate-printing
    /// mistake, and the batching model has no use for them.
    /// </summary>
    public int Copies => 1;

    /// <summary>
    /// When set, the spooler writes the driver's output to this file instead of the port.
    /// Used by the "preview this batch" action and by the automated tests, which print
    /// real batches through the real spooler to a virtual printer and then read the pages
    /// back to prove the page range is exact.
    /// </summary>
    public string? OutputFilePath { get; init; }

    public int PageCount => LastPage - FirstPage + 1;
}

/// <summary>Outcome of handing a batch to the spooler. Says nothing about paper yet.</summary>
public sealed record PrintResult
{
    public required bool Spooled { get; init; }

    /// <summary>Pages successfully drawn into the spool file.</summary>
    public int PagesRendered { get; init; }

    public string? Error { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset FinishedUtc { get; init; }

    /// <summary>
    /// True when the failure happened before any page was handed to the spooler, so we
    /// know for certain that nothing can print. This is the only kind of failure the
    /// engine is allowed to retry without asking a human.
    /// </summary>
    public bool FailedBeforeAnyOutput { get; init; }

    public static PrintResult Failure(string error, DateTimeOffset started, bool beforeAnyOutput) => new()
    {
        Spooled = false,
        Error = error,
        StartedUtc = started,
        FinishedUtc = DateTimeOffset.UtcNow,
        FailedBeforeAnyOutput = beforeAnyOutput,
    };
}

/// <summary>
/// Turns a page range into one Windows print job.
///
/// Abstracted so that the job engine can be driven by a simulated engine in tests,
/// including engines that fail mid-batch or pretend to spool without printing.
/// </summary>
public interface IPrintEngine
{
    string Name { get; }

    /// <summary>
    /// Submits the range. Returns once spooling has finished or failed.
    ///
    /// Implementations must set the spooler document name to
    /// <see cref="PrintRequest.DocumentToken"/> so the job can be identified later.
    /// </summary>
    Task<PrintResult> SubmitAsync(PrintRequest request, CancellationToken ct);
}
