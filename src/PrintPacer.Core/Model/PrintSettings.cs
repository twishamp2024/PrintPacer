using System.Globalization;

namespace PrintPacer.Core.Model;

// The numeric values of every enum in this file are what the job database stores. Append new
// members; never renumber existing ones.

/// <summary>Which pages of the source PDF are printed at all. The first stage of the pipeline.</summary>
public enum PageSelection
{
    AllPages = 0,
    OddPages = 1,
    EvenPages = 2,
}

/// <summary>How the selected pages are arranged on the paper.</summary>
public enum PrintLayout
{
    /// <summary>One page per side of paper, exactly as PrintPacer has always printed.</summary>
    Normal = 0,

    /// <summary>
    /// Two pages per side of a landscape sheet, reordered so that the folded, nested sheets
    /// read in order: a saddle-stitched booklet.
    /// </summary>
    Booklet = 1,

    /// <summary>
    /// Each sheet is one folded leaflet whose panels are consecutive pages of the PDF. Sheets
    /// are separate leaflets, never nested inside each other.
    /// </summary>
    Brochure = 2,
}

/// <summary>
/// One or both sides of the paper. Maps directly onto the Windows DEVMODE <c>dmDuplex</c>
/// field: long-edge binding is <c>DMDUP_VERTICAL</c>, short-edge binding <c>DMDUP_HORIZONTAL</c>.
/// </summary>
public enum DuplexMode
{
    SingleSided = 0,

    /// <summary>The sheet turns over like the pages of a book held upright.</summary>
    LongEdge = 1,

    /// <summary>The sheet turns over like a notepad or wall calendar.</summary>
    ShortEdge = 2,
}

/// <summary>Paper for booklet and brochure sheets. Normal jobs use the printer's default paper, as always.</summary>
public enum PaperChoice
{
    PrinterDefault = 0,
    A4 = 1,
    Letter = 2,
    Legal = 3,
    A3 = 4,
    A5 = 5,
    Tabloid = 6,
}

/// <summary>Which side of a closed booklet the fold is on.</summary>
public enum BookletBinding
{
    /// <summary>Left-to-right languages: the fold is on the left of the front cover.</summary>
    Left = 0,

    /// <summary>Right-to-left languages (Arabic, Hebrew, Urdu…): the fold is on the right.</summary>
    Right = 1,
}

public enum BrochureFold
{
    /// <summary>One fold, two panels per side: four panels from four pages.</summary>
    BiFold = 0,

    /// <summary>Two folds, letter-style (the right panel tucks in): six panels from six pages.</summary>
    TriFold = 1,
}

/// <summary>How each page is sized into its panel. Pages keep their aspect ratio either way.</summary>
public enum PageScaling
{
    /// <summary>Enlarged or reduced so the page fills its panel inside the margins.</summary>
    FitToPanel = 0,

    /// <summary>Printed at actual size unless it is too big for its panel, in which case it is reduced.</summary>
    ShrinkOnly = 1,
}

/// <summary>
/// What the user asked for on the setup screen's print settings: which pages, how they are
/// laid out, and whether both sides of the paper are used. Belongs to one job, is stored with
/// it, and is applied to every batch of it - including retries, remainders and batches sent
/// after a restart.
///
/// Immutable: a job's print settings cannot change once it exists, because the batch plan
/// (which physical sheet holds which pages) is derived from them.
/// </summary>
public sealed record PrintSettings
{
    public static readonly PrintSettings Default = new();

    public const double MaxMarginMm = 40;
    public const int MaxSheetsPerSignature = 50;

    public PageSelection PageSelection { get; init; } = PageSelection.AllPages;
    public PrintLayout Layout { get; init; } = PrintLayout.Normal;
    public DuplexMode Duplex { get; init; } = DuplexMode.SingleSided;

    // ---- Booklet and brochure sheets --------------------------------------------------

    public PaperChoice Paper { get; init; } = PaperChoice.PrinterDefault;

    /// <summary>Blank space around the edge of the sheet, in millimetres.</summary>
    public double MarginMm { get; init; } = 6;

    /// <summary>Total blank space centred on each fold, in millimetres.</summary>
    public double FoldGapMm { get; init; } = 12;

    public PageScaling Scaling { get; init; } = PageScaling.FitToPanel;

    // ---- Booklet only -------------------------------------------------------------

    public BookletBinding Binding { get; init; } = BookletBinding.Left;

    /// <summary>
    /// Sheets folded together as one section. Zero means every sheet nests into a single
    /// booklet; a thick document is easier to fold in sections of a few sheets each, which
    /// are then stacked in order.
    /// </summary>
    public int SheetsPerSignature { get; init; }

    /// <summary>
    /// When blank pages are needed to make the page count a multiple of four, put them before
    /// the last page rather than after it, so the last page stays on the outside back cover.
    /// </summary>
    public bool KeepLastPageAsBackCover { get; init; }

    // ---- Brochure only --------------------------------------------------------------

    public BrochureFold Fold { get; init; } = BrochureFold.BiFold;

    // ---- Derived -------------------------------------------------------------------

    public bool IsImposed => Layout != PrintLayout.Normal;

    public bool IsDoubleSided => Duplex != DuplexMode.SingleSided;

    /// <summary>True when one print unit is one page, which is how PrintPacer has always batched.</summary>
    public bool BatchesByPage => Layout == PrintLayout.Normal && Duplex == DuplexMode.SingleSided;

    public int PanelsPerSide => Layout switch
    {
        PrintLayout.Booklet => 2,
        PrintLayout.Brochure => Fold == BrochureFold.TriFold ? 3 : 2,
        _ => 1,
    };

    /// <summary>
    /// The things that make a configuration impossible, as sentences a user can act on. Empty
    /// when the settings can be printed (on a printer that supports them).
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (!Enum.IsDefined(PageSelection)) problems.Add("Choose which pages to print.");
        if (!Enum.IsDefined(Layout)) problems.Add("Choose a layout.");
        if (!Enum.IsDefined(Duplex)) problems.Add("Choose single- or double-sided printing.");

        if (IsImposed)
        {
            if (Duplex == DuplexMode.SingleSided)
            {
                problems.Add(Layout == PrintLayout.Booklet
                    ? "A booklet has pages on both sides of every sheet, so it must be printed double-sided."
                    : "A brochure has panels on both sides of the sheet, so it must be printed double-sided.");
            }

            if (!Enum.IsDefined(Paper)) problems.Add("Choose a paper size.");
            if (!Enum.IsDefined(Scaling)) problems.Add("Choose how pages are scaled.");
            if (double.IsNaN(MarginMm) || MarginMm < 0 || MarginMm > MaxMarginMm)
                problems.Add($"The margin must be between 0 and {MaxMarginMm:0} mm.");
            if (double.IsNaN(FoldGapMm) || FoldGapMm < 0 || FoldGapMm > 2 * MaxMarginMm)
                problems.Add($"The space at the fold must be between 0 and {2 * MaxMarginMm:0} mm.");
        }

        if (Layout == PrintLayout.Booklet)
        {
            if (!Enum.IsDefined(Binding)) problems.Add("Choose which side the booklet is bound on.");
            if (SheetsPerSignature < 0 || SheetsPerSignature > MaxSheetsPerSignature)
                problems.Add($"Sheets per section must be between 1 and {MaxSheetsPerSignature}, or all sheets in one booklet.");
        }

        if (Layout == PrintLayout.Brochure && !Enum.IsDefined(Fold)) problems.Add("Choose a brochure fold.");

        return problems;
    }

    /// <summary>The printer-side settings these choices need: sides, paper and orientation.</summary>
    public PrinterSetup PrinterSetup => new(
        Duplex,
        IsImposed ? Paper : PaperChoice.PrinterDefault,
        // Every imposed layout puts its panels side by side on a landscape sheet. Normal jobs
        // leave the orientation to the driver and rotate each page to fit, as before.
        IsImposed ? true : null);
}

/// <summary>
/// The settings a print job asks the printer driver for, as opposed to how PrintPacer arranges
/// the pages itself. Kept separate so it is obvious which choices depend on the printer.
/// </summary>
/// <param name="Landscape">True for landscape sheets; null leaves the driver's default.</param>
public sealed record PrinterSetup(DuplexMode Duplex, PaperChoice Paper, bool? Landscape)
{
    public static readonly PrinterSetup Default = new(DuplexMode.SingleSided, PaperChoice.PrinterDefault, null);
}

/// <summary>The wording used for print settings everywhere: setup screen, dashboard, log and report.</summary>
public static class PrintSettingsText
{
    public static string Describe(this PageSelection selection) => selection switch
    {
        PageSelection.OddPages => "Odd pages",
        PageSelection.EvenPages => "Even pages",
        _ => "All pages",
    };

    public static string Describe(this PrintLayout layout) => layout switch
    {
        PrintLayout.Booklet => "Booklet",
        PrintLayout.Brochure => "Brochure",
        _ => "Normal",
    };

    public static string Describe(this DuplexMode duplex) => duplex switch
    {
        DuplexMode.LongEdge => "Double-sided, flip on long edge",
        DuplexMode.ShortEdge => "Double-sided, flip on short edge",
        _ => "Single-sided",
    };

    public static string Describe(this PaperChoice paper) => paper switch
    {
        PaperChoice.A4 => "A4",
        PaperChoice.Letter => "Letter",
        PaperChoice.Legal => "Legal",
        PaperChoice.A3 => "A3",
        PaperChoice.A5 => "A5",
        PaperChoice.Tabloid => "Tabloid (11 × 17 in)",
        _ => "Printer's default paper",
    };

    public static string Describe(this BookletBinding binding) => binding switch
    {
        BookletBinding.Right => "Bound on the right",
        _ => "Bound on the left",
    };

    public static string Describe(this BrochureFold fold) => fold switch
    {
        BrochureFold.TriFold => "Tri-fold, 3 panels per side",
        _ => "Bi-fold, 2 panels per side",
    };

    public static string Describe(this PageScaling scaling) => scaling switch
    {
        PageScaling.ShrinkOnly => "Actual size, shrunk only if too big",
        _ => "Fit to panel",
    };

    /// <summary>"Odd pages · Booklet · Double-sided, flip on short edge": the whole configuration on one line.</summary>
    public static string Summary(this PrintSettings settings)
    {
        var parts = new List<string> { settings.PageSelection.Describe() };

        switch (settings.Layout)
        {
            case PrintLayout.Booklet:
                parts.Add($"Booklet ({settings.Binding.Describe().ToLowerInvariant()}" +
                          (settings.SheetsPerSignature > 0 ? $", sections of {settings.SheetsPerSignature} sheets" : "") + ")");
                break;
            case PrintLayout.Brochure:
                parts.Add($"Brochure ({settings.Fold.Describe().ToLowerInvariant()})");
                break;
        }

        parts.Add(settings.Duplex.Describe());

        if (settings.IsImposed)
            parts.Add($"{settings.Paper.Describe()} landscape");

        return string.Join(" · ", parts);
    }

    /// <summary>Margins and scaling of an imposed layout, e.g. "6 mm margins, 12 mm at each fold, fit to panel".</summary>
    public static string DescribeSpacing(this PrintSettings settings) =>
        string.Create(CultureInfo.CurrentCulture,
            $"{settings.MarginMm:0.#} mm margins, {settings.FoldGapMm:0.#} mm at each fold, " +
            $"{settings.Scaling.Describe().ToLowerInvariant()}");
}
