using FluentAssertions;
using PrintPacer.Core.Model;
using PrintPacer.Core.Printing;
using Xunit;

namespace PrintPacer.Tests;

/// <summary>
/// The print pipeline before batching: which pages are selected, and how the selected pages are
/// arranged on the sides of physical sheets. Every arrangement here is what the print engine
/// draws, so an error in this file is a wrong page on paper.
/// </summary>
public class PrintPlanTests
{
    // ---- Page selection ------------------------------------------------------------------

    [Theory]
    [InlineData(PageSelection.AllPages, 10, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    [InlineData(PageSelection.OddPages, 10, new[] { 1, 3, 5, 7, 9 })]
    [InlineData(PageSelection.EvenPages, 10, new[] { 2, 4, 6, 8, 10 })]
    [InlineData(PageSelection.OddPages, 9, new[] { 1, 3, 5, 7, 9 })]
    [InlineData(PageSelection.EvenPages, 9, new[] { 2, 4, 6, 8 })]
    [InlineData(PageSelection.AllPages, 1, new[] { 1 })]
    [InlineData(PageSelection.OddPages, 1, new[] { 1 })]
    [InlineData(PageSelection.AllPages, 2, new[] { 1, 2 })]
    [InlineData(PageSelection.OddPages, 2, new[] { 1 })]
    [InlineData(PageSelection.EvenPages, 2, new[] { 2 })]
    public void SelectsExactlyTheRequestedPages(PageSelection selection, int pages, int[] expected)
    {
        var plan = PrintPlan.Build(new PrintSettings { PageSelection = selection }, pages);

        plan.SelectedPages.Should().Equal(expected);
        // Single-sided normal printing: one selected page per sheet, in order, nothing else.
        plan.Unit.Should().Be(PrintUnit.Page);
        plan.Sheets.Select(s => s.Front.Panels.Single()).Should().Equal(expected.Select(p => (int?)p));
        plan.Sheets.Should().OnlyContain(s => s.Back == null, "single-sided sheets have nothing on the back");
        plan.TotalImpressions.Should().Be(expected.Length, "no blank pages are added");
    }

    [Fact]
    public void EvenPagesOfAOnePageDocumentIsRefusedRatherThanPrintingNothing()
    {
        var build = () => PrintPlan.Build(new PrintSettings { PageSelection = PageSelection.EvenPages }, 1);
        build.Should().Throw<ArgumentException>().WithMessage("*no even pages*");
    }

    [Fact]
    public void AllPagesIsTheDefaultAndBatchesExactlyAsBefore()
    {
        PrintSettings.Default.PageSelection.Should().Be(PageSelection.AllPages);
        PrintSettings.Default.Layout.Should().Be(PrintLayout.Normal);
        PrintSettings.Default.Duplex.Should().Be(DuplexMode.SingleSided);

        var plan = PrintPlan.Build(PrintSettings.Default, 203);
        var batches = PrintJob.PlanBatches("job", plan, 50);
        var legacy = PrintJob.PlanBatches("job", 203, 50);

        batches.Select(b => (b.FirstPage, b.LastPage, b.RangeLabel))
            .Should().Equal(legacy.Select(b => (b.FirstPage, b.LastPage, b.RangeLabel)));
        batches.Select(b => b.Impressions).Should().Equal(50, 50, 50, 50, 3);
    }

    [Fact]
    public void OddPagesBatchBySelectedPagesAndSayWhichSourcePagesThoseAre()
    {
        var plan = PrintPlan.Build(new PrintSettings { PageSelection = PageSelection.OddPages }, 100);
        plan.SelectedPages.Should().HaveCount(50);
        plan.UnitCount.Should().Be(50);

        // "10 pages per batch" means ten printed pages - ten selected pages - per batch.
        var batches = PrintJob.PlanBatches("job", plan, 10);
        batches.Should().HaveCount(5);
        batches[0].SourcePages.Should().Equal(1, 3, 5, 7, 9, 11, 13, 15, 17, 19);
        batches[0].RangeLabel.Should().Be("Odd pages 1–19");
        batches[4].RangeLabel.Should().Be("Odd pages 81–99");
        batches.SelectMany(b => b.SourcePages).Should().Equal(Enumerable.Range(1, 100).Where(p => p % 2 == 1));
        plan.DescribeSize().Should().Be("50 of 100 pages (odd pages)");
    }

    [Fact]
    public void EvenPagesBatchBySelectedPages()
    {
        var plan = PrintPlan.Build(new PrintSettings { PageSelection = PageSelection.EvenPages }, 25);
        var batches = PrintJob.PlanBatches("job", plan, 5);

        batches.Select(b => b.RangeLabel).Should().Equal("Even pages 2–10", "Even pages 12–20", "Even pages 22–24");
        batches.SelectMany(b => b.SourcePages).Should().Equal(2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24);
    }

    // ---- Double-sided -------------------------------------------------------------------------

    [Theory]
    [InlineData(DuplexMode.LongEdge)]
    [InlineData(DuplexMode.ShortEdge)]
    public void DoubleSidedPutsConsecutivePagesBackToBack(DuplexMode duplex)
    {
        var plan = PrintPlan.Build(new PrintSettings { Duplex = duplex }, 7);

        plan.Unit.Should().Be(PrintUnit.Sheet);
        plan.Sheets.Should().HaveCount(4);
        plan.Sheets.Select(s => (s.Front.Panels[0], s.Back?.Panels[0])).Should().Equal(
            ((int?)1, (int?)2), (3, 4), (5, 6), (7, null));
        plan.Sheets[^1].Back.Should().BeNull("the last odd page is printed on the front only, with no blank page");
        plan.TotalImpressions.Should().Be(7);
        plan.Settings.PrinterSetup.Duplex.Should().Be(duplex);
    }

    [Fact]
    public void DoubleSidedBatchesCountSheetsSoNoSheetIsSplit()
    {
        var plan = PrintPlan.Build(new PrintSettings { Duplex = DuplexMode.LongEdge }, 45);
        var batches = PrintJob.PlanBatches("job", plan, 10);

        batches.Should().HaveCount(3, "45 pages is 23 sheets; ten sheets per batch");
        batches.Select(b => b.Impressions).Should().Equal(20, 20, 5);
        batches[0].RangeLabel.Should().Be("Sheets 1–10 · pages 1–20");
        batches[2].RangeLabel.Should().Be("Sheets 21–23 · pages 41–45");

        // Every batch but the last starts a sheet with an odd page, so page n+1 is always on
        // the back of page n.
        foreach (var batch in batches)
            batch.SourcePages[0].Should().Be(2 * batch.FirstPage - 1);
    }

    [Fact]
    public void OddPagesDoubleSidedPrintsTheOddPagesBackToBack()
    {
        var plan = PrintPlan.Build(new PrintSettings { PageSelection = PageSelection.OddPages, Duplex = DuplexMode.LongEdge }, 10);

        plan.Sheets.Select(s => (s.Front.Panels[0], s.Back?.Panels[0])).Should().Equal(
            ((int?)1, (int?)3), (5, 7), (9, null));
        plan.DescribeRange(1, 2).Should().Be("Sheets 1–2 · odd pages 1–7");
    }

    // ---- Booklet ------------------------------------------------------------------------------

    [Fact]
    public void AnEightPageBookletIsTheTextbookArrangement()
    {
        var plan = Booklet(8);

        Sides(plan).Should().Equal(
            "1F 8|1", "1B 2|7",
            "2F 6|3", "2B 4|5");
        plan.Unit.Should().Be(PrintUnit.Sheet);
        plan.Settings.PrinterSetup.Should().Be(new PrinterSetup(DuplexMode.ShortEdge, PaperChoice.PrinterDefault, true));
    }

    [Theory]
    [InlineData(1, new[] { "1F ·|1" })]                                       // the blank back is not printed
    [InlineData(2, new[] { "1F ·|1", "1B 2|·" })]
    [InlineData(3, new[] { "1F ·|1", "1B 2|3" })]
    [InlineData(4, new[] { "1F 4|1", "1B 2|3" })]
    [InlineData(5, new[] { "1F ·|1", "1B 2|·", "2F ·|3", "2B 4|5" })]
    [InlineData(6, new[] { "1F ·|1", "1B 2|·", "2F 6|3", "2B 4|5" })]
    [InlineData(7, new[] { "1F ·|1", "1B 2|7", "2F 6|3", "2B 4|5" })]
    [InlineData(12, new[] { "1F 12|1", "1B 2|11", "2F 10|3", "2B 4|9", "3F 8|5", "3B 6|7" })]
    public void BookletsOfAnyLengthArePaddedAndOrdered(int pages, string[] expected)
    {
        Sides(Booklet(pages)).Should().Equal(expected);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(8)]
    [InlineData(10)] [InlineData(12)] [InlineData(13)] [InlineData(16)] [InlineData(31)] [InlineData(100)]
    public void AFoldedBookletReadsInOrder(int pages)
    {
        foreach (var binding in new[] { BookletBinding.Left, BookletBinding.Right })
        foreach (var section in new[] { 0, 1, 2, 3 })
        {
            var plan = PrintPlan.Build(new PrintSettings
            {
                Layout = PrintLayout.Booklet, Duplex = DuplexMode.ShortEdge, Binding = binding, SheetsPerSignature = section,
            }, pages);

            var padded = (pages + 3) / 4 * 4;
            plan.SheetCount.Should().Be(padded / 4);

            // Every source page appears exactly once, and there are exactly enough blanks.
            plan.Sheets.SelectMany(s => s.SourcePages).Order().Should().Equal(Enumerable.Range(1, pages));
            plan.BlankPanels.Should().Be(padded - pages);

            // Fold it: nest each section's sheets (outermost first) and read the pages off.
            FoldAndRead(plan, binding, section).Should().Equal(
                Enumerable.Range(1, pages).Select(p => (int?)p).Concat(Enumerable.Repeat<int?>(null, padded - pages)),
                $"{pages} pages, {binding} binding, sections of {section}");
        }
    }

    [Fact]
    public void RightBindingMirrorsEverySide()
    {
        Sides(Booklet(8, binding: BookletBinding.Right)).Should().Equal(
            "1F 1|8", "1B 7|2",
            "2F 3|6", "2B 5|4");
    }

    [Theory]
    [InlineData(16, 0, new[] { "1F 16|1", "1B 2|15", "2F 14|3", "2B 4|13", "3F 12|5", "3B 6|11", "4F 10|7", "4B 8|9" })]
    [InlineData(16, 2, new[] { "1F 8|1", "1B 2|7", "2F 6|3", "2B 4|5", "3F 16|9", "3B 10|15", "4F 14|11", "4B 12|13" })]
    [InlineData(10, 2, new[] { "1F 8|1", "1B 2|7", "2F 6|3", "2B 4|5", "3F ·|9", "3B 10|·" })]
    public void SectionsAreSmallBookletsInSequence(int pages, int sheetsPerSection, string[] expected)
    {
        Sides(Booklet(pages, sheetsPerSection: sheetsPerSection)).Should().Equal(expected);
    }

    [Fact]
    public void TheLastPageCanBeKeptAsTheBackCover()
    {
        // Five pages need three blanks. By default they go at the end, leaving the back cover
        // blank; with the option they go before page 5, which stays on the outside.
        Sides(Booklet(5)).Should().Equal("1F ·|1", "1B 2|·", "2F ·|3", "2B 4|5");
        Sides(Booklet(5, keepBackCover: true)).Should().Equal("1F 5|1", "1B 2|·", "2F ·|3", "2B 4|·");

        Sides(Booklet(10, keepBackCover: true))[0].Should().Be("1F 10|1");
        Sides(Booklet(8, keepBackCover: true)).Should().Equal(Sides(Booklet(8)), "nothing to move when no blanks are needed");
    }

    [Fact]
    public void NoBookletEverPrintsAnEmptySideBetweenTwoPrintedOnes()
    {
        // An all-blank side in the middle of a job could be skipped by a driver, which would
        // put every later page on the wrong side of the paper. Only a final blank back may
        // exist, and it is left off rather than printed.
        foreach (var pages in Enumerable.Range(1, 60))
        foreach (var section in new[] { 0, 1, 3 })
        foreach (var backCover in new[] { false, true })
        {
            var plan = PrintPlan.Build(new PrintSettings
            {
                Layout = PrintLayout.Booklet, Duplex = DuplexMode.ShortEdge,
                SheetsPerSignature = section, KeepLastPageAsBackCover = backCover,
            }, pages);

            plan.Sheets.SelectMany(s => s.Sides).Should().NotContain(side => side.IsBlank);
            plan.Sheets.Take(plan.SheetCount - 1).All(s => s.Back != null).Should().BeTrue(
                $"only the last sheet may go without a back ({pages} pages, sections of {section})");
        }
    }

    [Fact]
    public void BookletBatchesAreWholeSheets()
    {
        var plan = Booklet(40);                     // 10 sheets
        var batches = PrintJob.PlanBatches("job", plan, 4);

        batches.Select(b => (b.FirstPage, b.LastPage)).Should().Equal((1, 4), (5, 8), (9, 10));
        batches.Select(b => b.Impressions).Should().Equal(new[] { 8, 8, 4 }, "both sides of each sheet go in the same print job");
        batches[0].RangeLabel.Should().Be("Sheets 1–4 · pages 1–8, 33–40");

        // Together the batches are the booklet, sheet for sheet and side for side.
        batches.SelectMany(b => plan.SidesFor(b.FirstPage, b.LastPage))
            .Should().Equal(plan.Sheets.SelectMany(s => s.Sides));
    }

    [Fact]
    public void ABookletOfOddPagesImposesOnlyTheOddPages()
    {
        var plan = PrintPlan.Build(new PrintSettings
        {
            PageSelection = PageSelection.OddPages, Layout = PrintLayout.Booklet, Duplex = DuplexMode.ShortEdge,
        }, 16);

        // Selection happens before imposition: the booklet is made of pages 1, 3, ... 15.
        Sides(plan).Should().Equal("1F 15|1", "1B 3|13", "2F 11|5", "2B 7|9");
    }

    [Fact]
    public void ABookletMustBeDoubleSided()
    {
        new PrintSettings { Layout = PrintLayout.Booklet }.Problems()
            .Should().ContainSingle().Which.Should().Contain("double-sided");
        var build = () => PrintPlan.Build(new PrintSettings { Layout = PrintLayout.Booklet }, 8);
        build.Should().Throw<ArgumentException>();
    }

    // ---- Brochure -----------------------------------------------------------------------------

    [Fact]
    public void ABiFoldBrochureHasTheCoverOnTheRightOfTheOutside()
    {
        Sides(Brochure(4, BrochureFold.BiFold)).Should().Equal("1F 4|1", "1B 2|3");
    }

    [Fact]
    public void ATriFoldBrochureIsALetterFold()
    {
        // Outside: fold-in flap, back cover, front cover. Inside: panels 3, 4, 5.
        Sides(Brochure(6, BrochureFold.TriFold)).Should().Equal("1F 2|6|1", "1B 3|4|5");

        // Turning the sheet over on its short edge puts each panel's back directly behind it:
        // the front cover (outside right) backs the inside left, the flap backs the inside right.
        var sheet = Brochure(6, BrochureFold.TriFold).Sheets[0];
        BehindOnBack(sheet, 2).Should().Be(3);   // front cover  -> inside left
        BehindOnBack(sheet, 1).Should().Be(4);   // back cover   -> inside middle
        BehindOnBack(sheet, 0).Should().Be(5);   // fold-in flap -> inside right
    }

    [Theory]
    [InlineData(BrochureFold.BiFold, 9, new[] { "1F 4|1", "1B 2|3", "2F 8|5", "2B 6|7", "3F ·|9" })]
    [InlineData(BrochureFold.TriFold, 13, new[] { "1F 2|6|1", "1B 3|4|5", "2F 8|12|7", "2B 9|10|11", "3F ·|·|13" })]
    [InlineData(BrochureFold.TriFold, 4, new[] { "1F 2|·|1", "1B 3|4|·" })]
    [InlineData(BrochureFold.BiFold, 1, new[] { "1F ·|1" })]
    public void EachSheetIsItsOwnLeafletAndTheLastIsPadded(BrochureFold fold, int pages, string[] expected)
    {
        var plan = Brochure(pages, fold);
        Sides(plan).Should().Equal(expected);
        plan.Sheets.SelectMany(s => s.SourcePages).Order().Should().Equal(Enumerable.Range(1, pages));
    }

    [Fact]
    public void BrochureBatchesAreWholeLeaflets()
    {
        var plan = Brochure(30, BrochureFold.TriFold);    // five leaflets
        var batches = PrintJob.PlanBatches("job", plan, 2);

        batches.Select(b => b.PageCount).Should().Equal(2, 2, 1);
        batches.Select(b => b.Impressions).Should().Equal(4, 4, 2);
        batches[1].SourcePages.Should().Equal(Enumerable.Range(13, 12));
        batches[1].RangeLabel.Should().Be("Sheets 3–4 · pages 13–24");
        plan.Settings.PanelsPerSide.Should().Be(3);
    }

    [Fact]
    public void ABrochureIsNotABooklet()
    {
        // Eight pages: a booklet nests two sheets into one publication; a brochure makes two
        // separate leaflets of four panels each.
        Sides(Booklet(8)).Should().Equal("1F 8|1", "1B 2|7", "2F 6|3", "2B 4|5");
        Sides(Brochure(8, BrochureFold.BiFold)).Should().Equal("1F 4|1", "1B 2|3", "2F 8|5", "2B 6|7");
    }

    // ---- Resume points ------------------------------------------------------------------------

    [Fact]
    public void APartialCountOnlyProvesSheetsForSingleSidedWork()
    {
        var single = PrintPlan.Build(PrintSettings.Default, 50);
        single.UnitsProvenBy(11, 20, 4).Should().Be(4);
        single.UnitsProvenBy(11, 20, 40).Should().Be(10);

        var duplex = PrintPlan.Build(new PrintSettings { Duplex = DuplexMode.LongEdge }, 50);
        duplex.UnitsProvenBy(1, 10, 7).Should().Be(0,
            "seven printed sides could be seven fronts: a driver that duplexes by hand prints every front first");
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static PrintPlan Booklet(int pages, BookletBinding binding = BookletBinding.Left, int sheetsPerSection = 0,
        bool keepBackCover = false) => PrintPlan.Build(new PrintSettings
    {
        Layout = PrintLayout.Booklet,
        Duplex = DuplexMode.ShortEdge,
        Binding = binding,
        SheetsPerSignature = sheetsPerSection,
        KeepLastPageAsBackCover = keepBackCover,
    }, pages);

    private static PrintPlan Brochure(int pages, BrochureFold fold) => PrintPlan.Build(new PrintSettings
    {
        Layout = PrintLayout.Brochure,
        Duplex = DuplexMode.ShortEdge,
        Fold = fold,
    }, pages);

    /// <summary>"1F 8|1": sheet number, face, and panels left to right, "·" for blank.</summary>
    private static List<string> Sides(PrintPlan plan) => plan.Sheets
        .SelectMany(s => s.Sides)
        .Select(side => $"{side.Sheet}{(side.Face == SheetFace.Front ? "F" : "B")} " +
                        string.Join("|", side.Panels.Select(p => p?.ToString() ?? "·")))
        .ToList();

    /// <summary>The page printed on the back of the given front panel, after a short-edge turn.</summary>
    private static int? BehindOnBack(PlannedSheet sheet, int frontPanel) =>
        sheet.Back!.Panels[sheet.Back.Panels.Count - 1 - frontPanel];

    /// <summary>
    /// Physically assembles the booklet: each section's sheets nested with the first sheet
    /// outermost, folded, and the sections stacked in order. Returns the pages in reading
    /// order, as a reader turning the pages would meet them.
    /// </summary>
    private static List<int?> FoldAndRead(PrintPlan plan, BookletBinding binding, int sheetsPerSection)
    {
        var perSection = sheetsPerSection > 0 ? sheetsPerSection : plan.SheetCount;
        var read = new List<int?>();

        for (var start = 0; start < plan.SheetCount; start += perSection)
        {
            var section = plan.Sheets.Skip(start).Take(perSection).ToList();

            // For a left-bound booklet the right half of a sheet's front is a recto in the first
            // half of the section, and its left half a verso in the second half. Reading the
            // first half goes outermost-in; the second half innermost-out.
            var firstHalf = new List<int?>();
            var secondHalf = new List<int?>();
            foreach (var sheet in section)
            {
                var front = sheet.Front.Panels;
                var back = sheet.Back?.Panels ?? new int?[] { null, null };
                var (frontRight, frontLeft) = binding == BookletBinding.Left ? (front[1], front[0]) : (front[0], front[1]);
                var (backLeft, backRight) = binding == BookletBinding.Left ? (back[0], back[1]) : (back[1], back[0]);

                firstHalf.Add(frontRight);     // recto, turned to reveal...
                firstHalf.Add(backLeft);       // ...its verso
                secondHalf.Insert(0, frontLeft);
                secondHalf.Insert(0, backRight);
            }

            read.AddRange(firstHalf);
            read.AddRange(secondHalf);
        }

        return read;
    }
}
