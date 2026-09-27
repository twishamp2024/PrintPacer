using System.Drawing;
using FluentAssertions;
using PrintPacer.Core.Model;
using PrintPacer.Core.Printing;
using PrintPacer.Core.Simulation;
using PrintPacer.Core.Spooler;
using Xunit;
using Xunit.Abstractions;

namespace PrintPacer.Tests;

/// <summary>
/// Booklets, brochures and page selection through the real print path: the job's plan, the
/// real <see cref="PdfiumPrintEngine"/>, a real printer driver and the Windows spooler, with the
/// output written to a PDF file by Microsoft Print to PDF instead of to paper.
///
/// Every fixture page carries a barcode of its own page number. Each test reads the printed
/// file back, finds where each panel's page should be, and decodes the number actually printed
/// there - so a page on the wrong panel, the wrong side or the wrong sheet fails the test, as do
/// the wrong paper size or orientation. Print to PDF cannot print double-sided, so the sides of
/// each sheet come out as consecutive pages: front, back, front, back.
/// </summary>
public class ImposedPrintPathTests
{
    private const string PrintToPdf = "Microsoft Print to PDF";

    private readonly ITestOutputHelper _output;

    public ImposedPrintPathTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ABookletComesOutImposedOnLandscapeSheets()
    {
        if (!Installed()) return;

        var settings = new PrintSettings
        {
            Layout = PrintLayout.Booklet, Duplex = DuplexMode.ShortEdge, Paper = PaperChoice.A4, MarginMm = 8, FoldGapMm = 14,
        };

        // Ten pages: three sheets, two blanks, and the textbook order on the first two sheets.
        var (plan, pages) = await PrintPlanAsync(settings, documentPages: 10, firstUnit: 1, lastUnit: 3);

        pages.Should().HaveCount(6, "three sheets, front and back");
        pages.Should().OnlyContain(p => p.IsLandscape, "booklet sheets are landscape");
        pages.Should().OnlyContain(p => Math.Abs(p.WidthPt - 842) < 3 && Math.Abs(p.HeightPt - 595) < 3, "A4 was requested");

        Decode(plan, settings, pages).Should().Equal(
            "12→· 1", "2 11→·",     // sheet 1: blank | 1, then 2 | blank
            "10 3", "4 9",
            "8 5", "6 7");
    }

    [Fact]
    public async Task ABatchInTheMiddleOfABookletPrintsOnlyItsOwnSheets()
    {
        if (!Installed()) return;

        var settings = new PrintSettings
        {
            Layout = PrintLayout.Booklet, Duplex = DuplexMode.ShortEdge, Binding = BookletBinding.Right,
            Paper = PaperChoice.Letter, Scaling = PageScaling.ShrinkOnly,
        };

        // Sixteen pages is four sheets; print sheets 2-3 alone, as batch 2 of a two-sheet job would.
        var (plan, pages) = await PrintPlanAsync(settings, documentPages: 16, firstUnit: 2, lastUnit: 3);

        pages.Should().HaveCount(4);
        pages.Should().OnlyContain(p => Math.Abs(p.WidthPt - 792) < 3 && Math.Abs(p.HeightPt - 612) < 3,
            "Letter landscape was requested");

        // Right binding mirrors each side: the cover side of every spread is on the left.
        Decode(plan, settings, pages, firstUnit: 2).Should().Equal("3 14", "13 4", "5 12", "11 6");
    }

    [Fact]
    public async Task ATriFoldBrochurePutsSixPanelsOnTwoSides()
    {
        if (!Installed()) return;

        var settings = new PrintSettings
        {
            Layout = PrintLayout.Brochure, Fold = BrochureFold.TriFold, Duplex = DuplexMode.ShortEdge,
            Paper = PaperChoice.A4, MarginMm = 5, FoldGapMm = 10,
        };

        var (plan, pages) = await PrintPlanAsync(settings, documentPages: 6, firstUnit: 1, lastUnit: 1);

        pages.Should().HaveCount(2).And.OnlyContain(p => p.IsLandscape);
        Decode(plan, settings, pages).Should().Equal("2 6 1", "3 4 5");
    }

    [Fact]
    public async Task OddPagesPrintOnlyTheOddPages()
    {
        if (!Installed()) return;

        var settings = new PrintSettings { PageSelection = PageSelection.OddPages };
        var (plan, pages) = await PrintPlanAsync(settings, documentPages: 7, firstUnit: 1, lastUnit: 4);

        pages.Should().HaveCount(4);
        Decode(plan, settings, pages).Should().Equal("1", "3", "5", "7");
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private bool Installed()
    {
        if (new WindowsSpooler().ListPrinterNames().Contains(PrintToPdf)) return true;
        _output.WriteLine($"\"{PrintToPdf}\" is not installed; nothing was checked.");
        return false;
    }

    /// <summary>
    /// Prints units <paramref name="firstUnit"/>-<paramref name="lastUnit"/> of a job with these
    /// settings exactly as the engine would build the batch, single-sided because the virtual
    /// printer cannot turn paper over, and returns the printed file's pages.
    /// </summary>
    private async Task<(PrintPlan Plan, IReadOnlyList<InspectedPage> Pages)> PrintPlanAsync(
        PrintSettings settings, int documentPages, int firstUnit, int lastUnit)
    {
        var root = Path.Combine(Path.GetTempPath(), "printpacer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "fixture.pdf");
        TestPdfBuilder.Write(source, documentPages, "Imposition fixture", identityBarcode: true);

        var plan = PrintPlan.Build(settings, documentPages);
        var output = Path.Combine(root, "printed.pdf");

        var request = new PrintRequest
        {
            SourcePath = source,
            FirstPage = firstUnit,
            LastPage = lastUnit,
            Sides = plan.SidesFor(firstUnit, lastUnit),
            Setup = settings.PrinterSetup with { Duplex = DuplexMode.SingleSided },
            Imposition = settings.IsImposed ? ImpositionOptions.From(settings) : null,
            PrinterName = PrintToPdf,
            DocumentToken = $"PrintPacer TEST {Guid.NewGuid():N}",
            RenderDpi = 120,
            OutputFilePath = output,
        };

        var result = await new PdfiumPrintEngine().SubmitAsync(request, CancellationToken.None)
            .WaitAsync(TimeSpan.FromMinutes(2));

        result.Spooled.Should().BeTrue(result.Error);
        result.SetupRejected.Should().BeFalse();
        result.PagesRendered.Should().Be(request.PageCount);

        await WaitForFileToSettleAsync(output);
        var pages = OutputInspector.Render(output, dpi: 100);
        _output.WriteLine($"{settings.Summary()}: {pages.Count} page(s), " +
                          string.Join(", ", pages.Select(p => $"{p.WidthPt:0}x{p.HeightPt:0}")));
        return (plan, pages);
    }

    /// <summary>
    /// For each printed side, the page number decoded from each panel, left to right: "8 1".
    /// A panel that should be blank and is shows as "N→·" where N is its padding position, or
    /// simply "·"; a number means a page was found there.
    /// </summary>
    private static List<string> Decode(PrintPlan plan, PrintSettings settings, IReadOnlyList<InspectedPage> pages,
        int firstUnit = 1)
    {
        var sides = plan.SidesFor(firstUnit, plan.UnitCount).Take(pages.Count).ToList();
        var natural = ImpositionGeometry.PointsToHundredths(TestPdfBuilder.PageSizePt.Width, TestPdfBuilder.PageSizePt.Height);
        var results = new List<string>();

        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var sheet = ImpositionGeometry.PointsToHundredths(page.WidthPt, page.HeightPt);

            // Where the engine placed each page: the whole sheet for a normal job (Print to PDF
            // has no unprintable edge), the imposition geometry otherwise.
            var placements = settings.IsImposed
                ? ImpositionGeometry.PanelBoxes(sheet, sides[i].Panels.Count, settings.MarginMm, settings.FoldGapMm)
                    .Select(box => ImpositionGeometry.Place(box, natural, settings.Scaling)).ToList()
                : new List<RectangleF> { ImpositionGeometry.Place(new RectangleF(PointF.Empty, sheet), natural, PageScaling.FitToPanel) };

            var decoded = placements.Select((rect, panel) =>
            {
                var number = TestPdfBuilder.DecodeIdentity((fx, fy) =>
                    page.IsDarkAt(rect.X + fx * rect.Width, rect.Y + fy * rect.Height));
                var expectedBlank = sides[i].Panels[panel] is null;
                return number?.ToString() ?? (expectedBlank ? BlankLabel(plan, sides[i], panel) : "?");
            });

            results.Add(string.Join(" ", decoded));
        }

        return results;
    }

    /// <summary>A blank booklet panel is labelled with the padded page it stands for, e.g. "12→·".</summary>
    private static string BlankLabel(PrintPlan plan, PrintSide side, int panel)
    {
        if (plan.Settings.Layout != PrintLayout.Booklet) return "·";
        var padded = plan.SheetCount * 4;
        var s = side.Sheet - 1;
        var left = side.Face == SheetFace.Front ? padded - 2 * s : 2 + 2 * s;
        var right = side.Face == SheetFace.Front ? 1 + 2 * s : padded - 1 - 2 * s;
        var position = panel == 0 ? left : right;
        if (plan.Settings.Binding == BookletBinding.Right) position = panel == 0 ? right : left;
        return $"{position}→·";
    }

    private static async Task WaitForFileToSettleAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        long last = -1;
        while (DateTime.UtcNow < deadline)
        {
            var size = File.Exists(path) ? new FileInfo(path).Length : -1;
            if (size > 0 && size == last) return;
            last = size;
            await Task.Delay(500);
        }
    }
}
