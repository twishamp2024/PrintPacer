using PrintPacer.Core.Model;

namespace PrintPacer.Core.Printing;

/// <summary>
/// A request to print one batch as exactly one Windows print job.
///
/// What to draw is <see cref="Sides"/>: every side of every sheet in the batch, in order, as the
/// job's <see cref="PrintPlan"/> laid them out. A request without sides prints the contiguous
/// page range <see cref="FirstPage"/>-<see cref="LastPage"/>, one page per side, which is what a
/// plain job's plan produces anyway.
/// </summary>
public sealed record PrintRequest
{
    public required string SourcePath { get; init; }

    /// <summary>
    /// The batch's first and last print unit (see <see cref="PrintPlan.Unit"/>): source page
    /// numbers for a plain job, sheet numbers otherwise. Used for the range when
    /// <see cref="Sides"/> is null, and for logging.
    /// </summary>
    public required int FirstPage { get; init; }
    public required int LastPage { get; init; }

    /// <summary>The sides to print, in order. Null means pages <see cref="FirstPage"/>-<see cref="LastPage"/>, one per side.</summary>
    public IReadOnlyList<PrintSide>? Sides { get; init; }

    /// <summary>Sides, paper and orientation asked of the printer driver.</summary>
    public PrinterSetup Setup { get; init; } = PrinterSetup.Default;

    /// <summary>
    /// How pages are placed on an imposed side (booklet, brochure). Null for one page per side,
    /// which is drawn exactly as PrintPacer always has: fitted to the printable area and turned
    /// to match the paper.
    /// </summary>
    public ImpositionOptions? Imposition { get; init; }

    public required string PrinterName { get; init; }

    /// <summary>
    /// Document name handed to the spooler. Must be globally unique per attempt: it is the
    /// only reliable way to recognise our own job in the queue after a crash or restart.
    /// </summary>
    public required string DocumentToken { get; init; }

    public int RenderDpi { get; init; } = 300;
    public ColourMode ColourMode { get; init; } = ColourMode.Colour;
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

    /// <summary>What is actually drawn: <see cref="Sides"/>, or the page range one page per side.</summary>
    public IReadOnlyList<PrintSide> EffectiveSides => Sides ?? Enumerable.Range(FirstPage, Math.Max(0, LastPage - FirstPage + 1))
        .Select(page => new PrintSide(page, SheetFace.Front, new int?[] { page }))
        .ToList();

    /// <summary>Printed sides in the job: the pages the spooler counts and the printer's counter should advance by.</summary>
    public int PageCount => Sides?.Count ?? LastPage - FirstPage + 1;
}

/// <summary>Placement of pages on an imposed side, as chosen on the setup screen.</summary>
public sealed record ImpositionOptions(double MarginMm, double FoldGapMm, PageScaling Scaling)
{
    public static ImpositionOptions From(PrintSettings settings) =>
        new(settings.MarginMm, settings.FoldGapMm, settings.Scaling);
}

/// <summary>Outcome of handing a batch to the spooler. Says nothing about paper yet.</summary>
public sealed record PrintResult
{
    public required bool Spooled { get; init; }

    /// <summary>Pages successfully drawn into the spool file.</summary>
    public int PagesRendered { get; init; }

    public string? Error { get; init; }

    /// <summary>
    /// Set when a black and white batch spooled but the driver would not accept the black
    /// and white setting. Not a failure - the pages were sent, in greyscale - but the user
    /// is told rather than left believing the printer was asked for black ink only.
    /// </summary>
    public string? ColourWarning { get; init; }

    /// <summary>
    /// Set when nothing was printed because the printer driver would not accept the job's
    /// sides, paper or orientation. Retrying cannot help, so the engine stops and says why
    /// instead of sending it again.
    /// </summary>
    public bool SetupRejected { get; init; }

    /// <summary>Advice about the job's printer settings that did not stop it printing.</summary>
    public string? SetupWarning { get; init; }

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
