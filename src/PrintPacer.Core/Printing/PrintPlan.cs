using PrintPacer.Core.Billing;
using PrintPacer.Core.Model;

namespace PrintPacer.Core.Printing;

public enum SheetFace
{
    Front = 0,
    Back = 1,
}

/// <summary>
/// One side of one physical sheet, as it will be drawn: the source page for each panel from
/// left to right as that side faces you, or null for a blank panel.
/// </summary>
/// <param name="Sheet">1-based sheet number within the job, in print order.</param>
public sealed record PrintSide(int Sheet, SheetFace Face, IReadOnlyList<int?> Panels)
{
    public bool IsBlank => Panels.All(p => p is null);

    public IEnumerable<int> SourcePages => Panels.Where(p => p is not null).Select(p => p!.Value);

    /// <summary>"8 | 1", with "·" for a blank panel: how the sheet map on screen and in the log shows a side.</summary>
    public string Describe() => string.Join(" | ", Panels.Select(p => p?.ToString() ?? "·"));

    // Two sides are the same side when they carry the same pages in the same places, whichever
    // list instance holds them.
    public bool Equals(PrintSide? other) =>
        other is not null && Sheet == other.Sheet && Face == other.Face && Panels.SequenceEqual(other.Panels);

    public override int GetHashCode() =>
        Panels.Aggregate(HashCode.Combine(Sheet, Face), (hash, panel) => HashCode.Combine(hash, panel));
}

/// <summary>A physical sheet of paper and what goes on each side of it.</summary>
/// <param name="Back">Null when nothing is printed on the back.</param>
public sealed record PlannedSheet(int Number, PrintSide Front, PrintSide? Back)
{
    public IEnumerable<PrintSide> Sides => Back is null ? new[] { Front } : new[] { Front, Back };

    /// <summary>Sides the printer marks for this sheet: what its impression counter should advance by.</summary>
    public int Impressions => Back is null ? 1 : 2;

    public IEnumerable<int> SourcePages => Sides.SelectMany(s => s.SourcePages);
}

/// <summary>What one batch-sized step of a job is counted in.</summary>
public enum PrintUnit
{
    /// <summary>One page on one side of one sheet. Plain single-sided jobs, as PrintPacer always batched.</summary>
    Page,

    /// <summary>
    /// One physical sheet, with everything printed on both of its sides. Used whenever a sheet
    /// carries more than one page, so that a batch can never separate the front of a sheet from
    /// its back, or one half of a booklet spread from the other.
    /// </summary>
    Sheet,
}

/// <summary>
/// The whole print job, worked out before a single batch exists: which pages were selected,
/// how they are arranged on sheets, and what each side of each sheet carries.
///
/// This is the pipeline's middle stage. Page selection happens first, then the layout
/// (imposition) turns the selected pages into sheets, and only then are the sheets divided into
/// batches. Batching therefore never sees source page numbers for an imposed job - it counts
/// sheets - so it cannot split a booklet's spread or a sheet's two sides across two print jobs.
///
/// Pure and deterministic: the same settings and page count always give the same plan, which
/// is why a job only needs to store its settings, and why a batch recorded as "sheets 4-6"
/// still means exactly the same paper after a restart.
/// </summary>
public sealed class PrintPlan
{
    private readonly PlannedSheet[] _sheets;

    private PrintPlan(PrintSettings settings, int documentPages, IReadOnlyList<int> selected, PlannedSheet[] sheets)
    {
        Settings = settings;
        DocumentPages = documentPages;
        SelectedPages = selected;
        _sheets = sheets;
    }

    public PrintSettings Settings { get; }

    /// <summary>Pages in the source PDF.</summary>
    public int DocumentPages { get; }

    /// <summary>The source pages that will print, in document order, after page selection.</summary>
    public IReadOnlyList<int> SelectedPages { get; }

    public IReadOnlyList<PlannedSheet> Sheets => _sheets;

    public PrintUnit Unit => Settings.BatchesByPage ? PrintUnit.Page : PrintUnit.Sheet;

    /// <summary>How many units the job has, i.e. what "per batch" divides.</summary>
    public int UnitCount => _sheets.Length;

    public int SheetCount => _sheets.Length;

    public int PanelsPerSide => Settings.PanelsPerSide;

    /// <summary>Printed sides across the whole job.</summary>
    public int TotalImpressions => _sheets.Sum(s => s.Impressions);

    /// <summary>Panels left blank because the page count did not fill the last sheet or booklet.</summary>
    public int BlankPanels => _sheets.Sum(s => s.Sides.Sum(side => side.Panels.Count(p => p is null)))
                              + _sheets.Count(s => s.Back is null && Settings.IsImposed) * PanelsPerSide;

    public static PrintPlan Build(PrintSettings settings, int documentPages)
    {
        if (documentPages <= 0) throw new ArgumentOutOfRangeException(nameof(documentPages));
        if (settings.Problems() is { Count: > 0 } problems) throw new ArgumentException(problems[0], nameof(settings));

        var selected = SelectPages(settings.PageSelection, documentPages);
        if (selected.Count == 0)
        {
            throw new ArgumentException(
                $"The document has no {settings.PageSelection.Describe().ToLowerInvariant()} to print.", nameof(settings));
        }

        var sheets = settings.Layout switch
        {
            PrintLayout.Booklet => Imposition.Booklet(selected, settings.Binding, settings.SheetsPerSignature,
                settings.KeepLastPageAsBackCover),
            PrintLayout.Brochure => Imposition.Brochure(selected, settings.Fold),
            _ => Imposition.Normal(selected, settings.IsDoubleSided),
        };

        return new PrintPlan(settings, documentPages, selected, sheets);
    }

    /// <summary>The first stage: which source pages print at all.</summary>
    public static IReadOnlyList<int> SelectPages(PageSelection selection, int documentPages) => selection switch
    {
        PageSelection.OddPages => Enumerable.Range(1, documentPages).Where(p => p % 2 == 1).ToArray(),
        PageSelection.EvenPages => Enumerable.Range(1, documentPages).Where(p => p % 2 == 0).ToArray(),
        _ => Enumerable.Range(1, documentPages).ToArray(),
    };

    /// <summary>
    /// How many units a document has under the given settings without building the whole plan:
    /// what the setup screen needs to show batch counts as the user types.
    /// </summary>
    public static int CountUnits(PrintSettings settings, int documentPages) => Build(settings, documentPages).UnitCount;

    // ---- Units -> sheets -----------------------------------------------------------

    public IReadOnlyList<PlannedSheet> SheetsFor(int firstUnit, int lastUnit)
    {
        CheckRange(firstUnit, lastUnit);
        return _sheets[(firstUnit - 1)..lastUnit];
    }

    /// <summary>Every side to draw for a range of units, in the order the printer receives them.</summary>
    public IReadOnlyList<PrintSide> SidesFor(int firstUnit, int lastUnit) =>
        SheetsFor(firstUnit, lastUnit).SelectMany(s => s.Sides).ToList();

    /// <summary>What the printer's impression counter should advance by for a range of units.</summary>
    public int ImpressionsFor(int firstUnit, int lastUnit) =>
        SheetsFor(firstUnit, lastUnit).Sum(s => s.Impressions);

    /// <summary>The source pages a range of units puts on paper, in ascending order.</summary>
    public IReadOnlyList<int> SourcePagesFor(int firstUnit, int lastUnit) =>
        SheetsFor(firstUnit, lastUnit).SelectMany(s => s.SourcePages).Distinct().Order().ToList();

    /// <summary>
    /// How many whole units at the start of a range a count of printed sides proves complete.
    ///
    /// Only meaningful when each unit is exactly one side. A double-sided sheet counts two
    /// impressions, and a printer that turns the paper over by hand prints every front before
    /// any back, so "7 impressions" cannot be turned into "the first 3 sheets are done". Zero,
    /// which offers no resume point, is the only safe answer there.
    /// </summary>
    public int UnitsProvenBy(int firstUnit, int lastUnit, long impressions)
    {
        CheckRange(firstUnit, lastUnit);
        if (impressions <= 0) return 0;
        if (SheetsFor(firstUnit, lastUnit).Any(s => s.Impressions != 1)) return 0;
        return (int)Math.Min(impressions, lastUnit - firstUnit + 1);
    }

    // ---- Wording --------------------------------------------------------------------

    /// <summary>
    /// "200 pages", "50 of 100 pages (odd pages)", or "37 pages on 10 sheets, 3 blank pages added":
    /// how much paper the job is, before batching.
    /// </summary>
    public string DescribeSize()
    {
        var selected = SelectedPages.Count;
        var which = Settings.PageSelection == PageSelection.AllPages
            ? $"{selected} page{(selected == 1 ? "" : "s")}"
            : $"{selected} of {DocumentPages} pages ({Settings.PageSelection.Describe().ToLowerInvariant()})";

        if (Unit == PrintUnit.Page) return which;

        var text = $"{which} on {SheetCount} sheet{(SheetCount == 1 ? "" : "s")}";
        var blanks = BlankPanels;
        if (Settings.IsImposed && blanks > 0) text += $", {blanks} blank page{(blanks == 1 ? "" : "s")} added";
        return text;
    }

    public string UnitName(int count) => Unit == PrintUnit.Page
        ? (count == 1 ? "page" : "pages")
        : (count == 1 ? "sheet" : "sheets");

    /// <summary>
    /// The label of a batch. A plain job reads "Pages 51–100" exactly as it always has; other
    /// jobs say which sheets and which source pages are on them.
    /// </summary>
    public string DescribeRange(int firstUnit, int lastUnit)
    {
        CheckRange(firstUnit, lastUnit);

        if (Unit == PrintUnit.Page)
        {
            var first = _sheets[firstUnit - 1].Front.Panels[0]!.Value;
            var last = _sheets[lastUnit - 1].Front.Panels[0]!.Value;
            if (first == last) return $"Page {first}";
            return Settings.PageSelection switch
            {
                PageSelection.OddPages => $"Odd pages {first}–{last}",
                PageSelection.EvenPages => $"Even pages {first}–{last}",
                _ => $"Pages {first}–{last}",
            };
        }

        var sheets = firstUnit == lastUnit ? $"Sheet {firstUnit}" : $"Sheets {firstUnit}–{lastUnit}";
        var pages = SourcePagesFor(firstUnit, lastUnit);
        return pages.Count == 0 ? $"{sheets} · blank" : $"{sheets} · {DescribePages(pages, 44)}";
    }

    /// <summary>"pages 1–10, 31–40", or "odd pages 1–19": source pages as a reader would write them.</summary>
    public string DescribePages(IReadOnlyList<int> pages, int maxLength = int.MaxValue)
    {
        if (pages.Count == 1) return $"page {pages[0]}";

        var step = Settings.PageSelection == PageSelection.AllPages ? 1 : 2;
        var parts = new List<string>();
        for (var i = 0; i < pages.Count; i++)
        {
            var start = pages[i];
            while (i + 1 < pages.Count && pages[i + 1] == pages[i] + step) i++;
            parts.Add(start == pages[i] ? $"{start}" : $"{start}–{pages[i]}");
        }

        var prefix = Settings.PageSelection switch
        {
            PageSelection.OddPages => "odd pages ",
            PageSelection.EvenPages => "even pages ",
            _ => "pages ",
        };

        return prefix + PageColourMap.Shorten(string.Join(", ", parts), Math.Max(8, maxLength - prefix.Length));
    }

    /// <summary>
    /// The first few sheets as "Sheet 1  front 8 | 1   back 2 | 7", so the user can check the
    /// arrangement before anything is printed.
    /// </summary>
    public IReadOnlyList<string> DescribeSheets(int maxSheets)
    {
        var lines = new List<string>();
        foreach (var sheet in _sheets.Take(maxSheets))
        {
            var line = $"Sheet {sheet.Number,-3} front  {sheet.Front.Describe()}";
            if (sheet.Back is { } back) line += $"    back  {back.Describe()}";
            lines.Add(line);
        }

        if (_sheets.Length > maxSheets) lines.Add($"… {_sheets.Length - maxSheets} more sheet(s)");
        return lines;
    }

    private void CheckRange(int firstUnit, int lastUnit)
    {
        if (firstUnit < 1 || lastUnit < firstUnit || lastUnit > _sheets.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(firstUnit),
                $"Units {firstUnit}-{lastUnit} are outside this job's 1-{_sheets.Length}.");
        }
    }
}

/// <summary>
/// Page imposition: which page goes on which panel of which side of which sheet.
///
/// Every arrangement assumes the sheet is turned over on the edge that its fold runs parallel
/// to (short-edge for the landscape sheets used here), so that the back of a panel is the panel
/// mirrored across the sheet: the back of the rightmost front panel is the leftmost back panel.
/// </summary>
public static class Imposition
{
    /// <summary>One page per side; two per sheet when double-sided. The last back is left off when there is nothing for it.</summary>
    public static PlannedSheet[] Normal(IReadOnlyList<int> pages, bool doubleSided)
    {
        if (!doubleSided)
        {
            return pages.Select((page, i) => new PlannedSheet(i + 1,
                new PrintSide(i + 1, SheetFace.Front, new int?[] { page }), null)).ToArray();
        }

        var sheets = new PlannedSheet[(pages.Count + 1) / 2];
        for (var i = 0; i < sheets.Length; i++)
        {
            var number = i + 1;
            var front = new PrintSide(number, SheetFace.Front, new int?[] { pages[2 * i] });
            var back = 2 * i + 1 < pages.Count
                ? new PrintSide(number, SheetFace.Back, new int?[] { pages[2 * i + 1] })
                : null;
            sheets[i] = new PlannedSheet(number, front, back);
        }
        return sheets;
    }

    /// <summary>
    /// A saddle-stitched booklet. The pages are padded with blanks to a multiple of four, then
    /// each section of sheets is imposed so that, nested and folded, it reads in order. For a
    /// section of <c>n</c> pages, sheet <c>s</c> (0-based, outermost first) carries
    /// <c>n-2s | 1+2s</c> on the front and <c>2+2s | n-1-2s</c> on the back, which for eight
    /// pages is the familiar 8|1, 2|7, 6|3, 4|5. Right binding mirrors both sides.
    /// </summary>
    /// <param name="sheetsPerSignature">Sheets per folded section; zero for a single section.</param>
    public static PlannedSheet[] Booklet(IReadOnlyList<int> pages, BookletBinding binding,
        int sheetsPerSignature, bool keepLastPageAsBackCover)
    {
        var padded = (pages.Count + 3) / 4 * 4;
        var blanks = padded - pages.Count;

        // The reading order of the finished booklet, blanks included.
        var logical = new List<int?>(padded);
        if (keepLastPageAsBackCover && blanks > 0 && pages.Count >= 2)
        {
            logical.AddRange(pages.Take(pages.Count - 1).Select(p => (int?)p));
            logical.AddRange(Enumerable.Repeat<int?>(null, blanks));
            logical.Add(pages[^1]);
        }
        else
        {
            logical.AddRange(pages.Select(p => (int?)p));
            logical.AddRange(Enumerable.Repeat<int?>(null, blanks));
        }

        var totalSheets = padded / 4;
        var perSection = sheetsPerSignature > 0 ? sheetsPerSignature : totalSheets;

        var sheets = new List<PlannedSheet>(totalSheets);
        for (var sectionStart = 0; sectionStart < totalSheets; sectionStart += perSection)
        {
            var sectionSheets = Math.Min(perSection, totalSheets - sectionStart);
            var basePage = sectionStart * 4;          // 0-based index into the reading order
            var n = sectionSheets * 4;

            for (var s = 0; s < sectionSheets; s++)
            {
                var number = sheets.Count + 1;
                int? At(int onePagedIndex) => logical[basePage + onePagedIndex - 1];

                var front = new[] { At(n - 2 * s), At(1 + 2 * s) };
                var back = new[] { At(2 + 2 * s), At(n - 1 - 2 * s) };

                if (binding == BookletBinding.Right)
                {
                    Array.Reverse(front);
                    Array.Reverse(back);
                }

                sheets.Add(Sheet(number, front, back, isLast: number == totalSheets));
            }
        }

        return sheets.ToArray();
    }

    /// <summary>
    /// Folded leaflets, one per sheet, each made from the next group of consecutive pages.
    ///
    /// Bi-fold (4 pages): 1 front cover, 2 and 3 inside left and right, 4 back cover. The
    /// outside of the sheet is 4 | 1 and the inside 2 | 3.
    ///
    /// Tri-fold, letter fold (6 pages): 1 front cover, 2 the flap that folds in, 3-5 the inside
    /// from left to right, 6 back cover. The outside is 2 | 6 | 1 and the inside 3 | 4 | 5; the
    /// right-hand inside panel folds in first and the front cover closes over it.
    /// </summary>
    public static PlannedSheet[] Brochure(IReadOnlyList<int> pages, BrochureFold fold)
    {
        var panels = fold == BrochureFold.TriFold ? 6 : 4;
        var count = (pages.Count + panels - 1) / panels;
        var sheets = new PlannedSheet[count];

        for (var i = 0; i < count; i++)
        {
            int? P(int panel) => i * panels + panel - 1 < pages.Count ? pages[i * panels + panel - 1] : null;

            var (front, back) = fold == BrochureFold.TriFold
                ? (new[] { P(2), P(6), P(1) }, new[] { P(3), P(4), P(5) })
                : (new[] { P(4), P(1) }, new[] { P(2), P(3) });

            sheets[i] = Sheet(i + 1, front, back, isLast: i == count - 1);
        }

        return sheets;
    }

    /// <summary>
    /// A sheet with both sides. An entirely blank back is left off, but only on the last sheet
    /// of the job: anywhere else, skipping a side would put the next sheet's front on this
    /// sheet's back.
    /// </summary>
    private static PlannedSheet Sheet(int number, int?[] front, int?[] back, bool isLast)
    {
        var frontSide = new PrintSide(number, SheetFace.Front, front);
        var backSide = new PrintSide(number, SheetFace.Back, back);
        return new PlannedSheet(number, frontSide, isLast && backSide.IsBlank ? null : backSide);
    }
}
