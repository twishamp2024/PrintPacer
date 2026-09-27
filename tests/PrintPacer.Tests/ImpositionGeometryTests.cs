using System.Drawing;
using FluentAssertions;
using PrintPacer.Core.Model;
using PrintPacer.Core.Printing;
using Xunit;

namespace PrintPacer.Tests;

/// <summary>
/// Where each page of a booklet or brochure side lands: margins at the edge of the sheet, the
/// gap at each fold, and how pages are scaled into their panels. All in hundredths of an inch
/// from the corner of the paper, which is exactly what the print engine draws with.
/// </summary>
public class ImpositionGeometryTests
{
    private static readonly SizeF A4Landscape = ImpositionGeometry.Landscape(ImpositionGeometry.NominalPaper(PaperChoice.A4)!.Value);
    private static readonly SizeF A4Portrait = ImpositionGeometry.NominalPaper(PaperChoice.A4)!.Value;

    [Fact]
    public void PaperSizesAreTheStandardOnes()
    {
        Mm(A4Portrait.Width).Should().BeApproximately(210, 0.01);
        Mm(A4Portrait.Height).Should().BeApproximately(297, 0.01);
        ImpositionGeometry.NominalPaper(PaperChoice.Letter).Should().Be(new SizeF(850, 1100));
        ImpositionGeometry.NominalPaper(PaperChoice.PrinterDefault).Should().BeNull();
        A4Landscape.Width.Should().BeGreaterThan(A4Landscape.Height);
    }

    [Fact]
    public void TwoPanelsSplitTheSheetAtTheFoldWithMarginsAndGap()
    {
        var boxes = ImpositionGeometry.PanelBoxes(A4Landscape, 2, marginMm: 8, foldGapMm: 12);

        boxes.Should().HaveCount(2);
        Mm(boxes[0].Left).Should().BeApproximately(8, 0.01, "outer margin on the left edge");
        Mm(boxes[1].Right).Should().BeApproximately(297 - 8, 0.01, "outer margin on the right edge");
        Mm(boxes[0].Right).Should().BeApproximately(148.5 - 6, 0.01, "half the fold gap left of the fold");
        Mm(boxes[1].Left).Should().BeApproximately(148.5 + 6, 0.01, "half the fold gap right of the fold");
        Mm(boxes[0].Top).Should().BeApproximately(8, 0.01);
        Mm(boxes[0].Bottom).Should().BeApproximately(210 - 8, 0.01);
        boxes[0].Width.Should().BeApproximately(boxes[1].Width, 0.01f, "both pages of a spread get the same space");
        boxes[0].Height.Should().Be(boxes[1].Height);
    }

    [Fact]
    public void ThreePanelsAreEqualThirds()
    {
        var boxes = ImpositionGeometry.PanelBoxes(A4Landscape, 3, marginMm: 5, foldGapMm: 10);

        Mm(boxes[0].Left).Should().BeApproximately(5, 0.01);
        Mm(boxes[0].Right).Should().BeApproximately(99 - 5, 0.01);
        Mm(boxes[1].Left).Should().BeApproximately(99 + 5, 0.01);
        Mm(boxes[1].Right).Should().BeApproximately(198 - 5, 0.01);
        Mm(boxes[2].Left).Should().BeApproximately(198 + 5, 0.01);
        Mm(boxes[2].Right).Should().BeApproximately(297 - 5, 0.01);
        boxes[0].Width.Should().BeApproximately(boxes[2].Width, 0.01f);
        boxes[1].Width.Should().BeLessThan(boxes[0].Width, "the middle panel gives up gap on both sides");
    }

    [Fact]
    public void MarginsThatLeaveNoRoomAreRefused()
    {
        var act = () => ImpositionGeometry.PanelBoxes(A4Landscape, 2, marginMm: 40, foldGapMm: 80);
        act.Should().NotThrow("there is still room");

        var tooMuch = () => ImpositionGeometry.PanelBoxes(ImpositionGeometry.Landscape(
            ImpositionGeometry.NominalPaper(PaperChoice.A5)!.Value), 3, marginMm: 40, foldGapMm: 80);
        tooMuch.Should().Throw<ArgumentException>().WithMessage("*no room*");
    }

    [Fact]
    public void AnA4PageFitsHalfAnA4SheetAtAboutSeventyPercent()
    {
        var box = ImpositionGeometry.PanelBoxes(A4Landscape, 2, 0, 0)[0];
        var placed = ImpositionGeometry.Place(box, A4Portrait, PageScaling.FitToPanel);

        (placed.Width / A4Portrait.Width).Should().BeApproximately(0.7071f, 0.001f, "A4 to A5 is 1/√2");
        placed.Width.Should().BeApproximately(box.Width, 0.5f, "an A5 half-sheet is exactly an A4 page scaled down");
        placed.Height.Should().BeApproximately(box.Height, 0.5f);
    }

    [Fact]
    public void FitKeepsTheAspectRatioAndCentres()
    {
        var box = new RectangleF(100, 100, 400, 800);
        var page = new SizeF(1100, 850);     // a landscape page in a portrait panel

        var placed = ImpositionGeometry.Place(box, page, PageScaling.FitToPanel);

        (placed.Width / placed.Height).Should().BeApproximately(1100f / 850f, 0.001f);
        placed.Width.Should().BeApproximately(400, 0.01f, "limited by the panel's width");
        (placed.Left - box.Left).Should().BeApproximately(0, 0.01f);
        (placed.Top - box.Top).Should().BeApproximately(box.Bottom - placed.Bottom, 0.01f, "centred vertically");
    }

    [Fact]
    public void ShrinkOnlyNeverEnlargesButStillShrinksWhatDoesNotFit()
    {
        var box = new RectangleF(0, 0, 500, 700);

        var small = ImpositionGeometry.Place(box, new SizeF(300, 400), PageScaling.ShrinkOnly);
        small.Size.Should().Be(new SizeF(300, 400), "a page that fits prints at actual size");
        small.Location.Should().Be(new PointF(100, 150), "centred");

        ImpositionGeometry.Place(box, new SizeF(300, 400), PageScaling.FitToPanel).Width
            .Should().BeApproximately(500, 0.01f, "fit enlarges the same page");

        var large = ImpositionGeometry.Place(box, new SizeF(1000, 1400), PageScaling.ShrinkOnly);
        large.Width.Should().BeApproximately(500, 0.01f, "a page that does not fit is reduced");
        ImpositionGeometry.ScaleFor(box, new SizeF(1000, 1400), PageScaling.ShrinkOnly).Should().BeApproximately(0.5f, 0.001f);
    }

    [Fact]
    public void EveryPlacedPageStaysInsideItsPanel()
    {
        var pages = new[] { new SizeF(827, 1169), new SizeF(1169, 827), new SizeF(400, 400), new SizeF(2000, 300) };
        foreach (var panels in new[] { 2, 3 })
        foreach (var scaling in Enum.GetValues<PageScaling>())
        foreach (var page in pages)
        {
            var boxes = ImpositionGeometry.PanelBoxes(A4Landscape, panels, 6, 12);
            foreach (var box in boxes)
            {
                var placed = ImpositionGeometry.Place(box, page, scaling);
                placed.Left.Should().BeGreaterThanOrEqualTo(box.Left - 0.01f);
                placed.Right.Should().BeLessThanOrEqualTo(box.Right + 0.01f);
                placed.Top.Should().BeGreaterThanOrEqualTo(box.Top - 0.01f);
                placed.Bottom.Should().BeLessThanOrEqualTo(box.Bottom + 0.01f);
            }
        }
    }

    [Fact]
    public void ImposedLayoutsAskForLandscapeAndNormalJobsLeaveOrientationAlone()
    {
        new PrintSettings().PrinterSetup.Landscape.Should().BeNull();
        new PrintSettings { Duplex = DuplexMode.LongEdge }.PrinterSetup.Should()
            .Be(new PrinterSetup(DuplexMode.LongEdge, PaperChoice.PrinterDefault, null));

        new PrintSettings { Layout = PrintLayout.Booklet, Duplex = DuplexMode.ShortEdge, Paper = PaperChoice.A3 }
            .PrinterSetup.Should().Be(new PrinterSetup(DuplexMode.ShortEdge, PaperChoice.A3, true));

        // Paper is a booklet and brochure setting; a normal job keeps using the printer's paper.
        new PrintSettings { Paper = PaperChoice.A3 }.PrinterSetup.Paper.Should().Be(PaperChoice.PrinterDefault);
    }

    private static double Mm(float hundredths) => ImpositionGeometry.HundredthsToMm(hundredths);
}
