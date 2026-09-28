using PrintPacer.Core.Billing;

namespace PrintPacer.Core.Model;

/// <summary>
/// Everything the user chooses about one print job: where it goes, how it is laid out, how it
/// is paced, when it may start and whether it is billed.
///
/// An immutable value. Every job holds its own, so changing one job's configuration replaces
/// that job's value and cannot reach any other job, and the defaults a job was created from
/// are copied, never shared. New per-job settings belong here, so the queue, the store and the
/// setup screen pick them up together.
/// </summary>
public sealed record JobConfiguration
{
    public required string PrinterName { get; init; }

    /// <summary>Print units per batch: pages for a plain single-sided job, sheets otherwise.</summary>
    public int PagesPerBatch { get; init; } = 50;

    public int DelayMinutes { get; init; } = 60;

    /// <summary>
    /// <see cref="StartMode.Immediately"/> starts the job as soon as its turn in the queue comes;
    /// <see cref="StartMode.AtTime"/> additionally waits for <see cref="ScheduledStartUtc"/>.
    /// </summary>
    public StartMode StartMode { get; init; } = StartMode.Immediately;
    public DateTimeOffset? ScheduledStartUtc { get; init; }

    public int RenderDpi { get; init; } = 300;
    public ColourMode ColourMode { get; init; } = ColourMode.Colour;
    public PrintSettings PrintSettings { get; init; } = PrintSettings.Default;

    /// <summary>Prices and page colours for the invoice, or null for no invoice.</summary>
    public InvoiceRequest? Invoice { get; init; }

    public const int MaxDelayMinutes = 24 * 60;

    /// <summary>
    /// The initial configuration of a new job, copied from the defaults in the settings. Only a
    /// starting point: the job keeps its own copy, and nothing done to the job changes the
    /// defaults. Page selection and layout always start plain, so a PDF never becomes a booklet
    /// because a default said so.
    /// </summary>
    public static JobConfiguration FromDefaults(AppSettings settings, string printerName, string? pageColourMap = null) => new()
    {
        PrinterName = printerName,
        PagesPerBatch = Math.Max(1, settings.DefaultPagesPerBatch),
        DelayMinutes = Math.Clamp(settings.DefaultDelayMinutes, 0, MaxDelayMinutes),
        StartMode = StartMode.Immediately,
        RenderDpi = settings.RenderDpi,
        ColourMode = settings.ColourMode,
        PrintSettings = PrintSettings.Default with { Duplex = settings.DefaultDuplex },
        Invoice = settings.PrintInvoice
            ? new InvoiceRequest(settings.BlackWhitePagePrice, settings.ColourPagePrice, pageColourMap)
            : null,
    };

    /// <summary>
    /// Whether an invoice for this configuration still needs the colour of every page, which is
    /// found by rendering the document. Black and white bills every page as black and white.
    /// </summary>
    public bool NeedsPageColours(int pageCount) =>
        Invoice is not null && ColourMode != ColourMode.BlackAndWhite &&
        !PageColourMap.IsValid(Invoice.PageColourMap, pageCount);

    /// <summary>Reasons this configuration cannot print a document of this many pages, in plain words.</summary>
    public IReadOnlyList<string> Problems(int pageCount)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(PrinterName)) problems.Add("Choose a printer.");
        if (PagesPerBatch < 1) problems.Add("The batch size must be at least 1.");
        if (DelayMinutes is < 0 or > MaxDelayMinutes) problems.Add("The rest between batches must be between 0 minutes and 24 hours.");
        if (StartMode == StartMode.AtTime && ScheduledStartUtc is null) problems.Add("Choose a start time.");

        problems.AddRange(PrintSettings.Problems());

        if (Invoice is not null)
        {
            if (Invoice.BlackWhitePagePrice < 0 || Invoice.ColourPagePrice < 0)
                problems.Add("Page prices cannot be negative.");

            // Without a colour map the bill could only guess, and a guessed bill is a wrong one.
            if (NeedsPageColours(pageCount))
                problems.Add("The invoice needs the colour of every page.");
        }

        return problems;
    }
}
