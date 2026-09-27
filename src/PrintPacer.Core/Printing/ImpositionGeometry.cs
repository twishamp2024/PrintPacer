using System.Drawing;
using PrintPacer.Core.Model;

namespace PrintPacer.Core.Printing;

/// <summary>
/// Where each page of an imposed side lands on the paper.
///
/// Everything here is in hundredths of an inch, measured from the top-left corner of the
/// physical sheet as the printer sees it after orientation is applied: the unit and origin of
/// <see cref="System.Drawing.Printing.PrintPageEventArgs.PageBounds"/>. Keeping one unit and one
/// origin means the numbers the tests check are exactly the numbers drawn.
/// </summary>
public static class ImpositionGeometry
{
    public const float HundredthsPerMm = 100f / 25.4f;

    public static float MmToHundredths(double mm) => (float)(mm * HundredthsPerMm);

    public static double HundredthsToMm(float hundredths) => hundredths / HundredthsPerMm;

    /// <summary>
    /// The area each panel's page may occupy: the sheet divided into equal columns at the
    /// folds, less the margin at the sheet's edges and half the fold gap either side of each fold.
    /// </summary>
    /// <exception cref="ArgumentException">The margins leave no room for the pages.</exception>
    public static RectangleF[] PanelBoxes(SizeF sheet, int panels, double marginMm, double foldGapMm)
    {
        if (panels < 1) throw new ArgumentOutOfRangeException(nameof(panels));

        var margin = MmToHundredths(marginMm);
        var halfGap = MmToHundredths(foldGapMm) / 2f;
        var column = sheet.Width / panels;

        var boxes = new RectangleF[panels];
        for (var i = 0; i < panels; i++)
        {
            var left = i * column + (i == 0 ? margin : halfGap);
            var right = (i + 1) * column - (i == panels - 1 ? margin : halfGap);
            var top = margin;
            var bottom = sheet.Height - margin;

            if (right - left < 1 || bottom - top < 1)
            {
                throw new ArgumentException(
                    $"A {marginMm:0.#} mm margin and {foldGapMm:0.#} mm fold gap leave no room for the pages " +
                    $"on a {HundredthsToMm(sheet.Width):0} × {HundredthsToMm(sheet.Height):0} mm sheet.");
            }

            boxes[i] = RectangleF.FromLTRB(left, top, right, bottom);
        }

        return boxes;
    }

    /// <summary>
    /// A page of the given natural size placed in a panel box: scaled according to
    /// <paramref name="scaling"/>, aspect ratio kept, centred.
    /// </summary>
    public static RectangleF Place(RectangleF box, SizeF page, PageScaling scaling)
    {
        if (page.Width <= 0 || page.Height <= 0) throw new ArgumentOutOfRangeException(nameof(page));

        var scale = Math.Min(box.Width / page.Width, box.Height / page.Height);
        if (scaling == PageScaling.ShrinkOnly) scale = Math.Min(1f, scale);

        var width = page.Width * scale;
        var height = page.Height * scale;
        return new RectangleF(box.X + (box.Width - width) / 2f, box.Y + (box.Height - height) / 2f, width, height);
    }

    /// <summary>The scale a page of this size is printed at, for the setup summary ("pages at 71%").</summary>
    public static float ScaleFor(RectangleF box, SizeF page, PageScaling scaling) =>
        Place(box, page, scaling).Width / page.Width;

    /// <summary>Points (1/72 in, the PDF unit) to hundredths of an inch.</summary>
    public static SizeF PointsToHundredths(double widthPt, double heightPt) =>
        new((float)(widthPt * 100 / 72), (float)(heightPt * 100 / 72));

    /// <summary>
    /// Nominal portrait size of a paper choice in hundredths of an inch, for summaries worked out
    /// before a printer is asked. Null for the printer's default paper, which only the driver knows.
    /// </summary>
    public static SizeF? NominalPaper(PaperChoice paper) => paper switch
    {
        PaperChoice.A4 => Mm(210, 297),
        PaperChoice.Letter => new SizeF(850, 1100),
        PaperChoice.Legal => new SizeF(850, 1400),
        PaperChoice.A3 => Mm(297, 420),
        PaperChoice.A5 => Mm(148, 210),
        PaperChoice.Tabloid => new SizeF(1100, 1700),
        _ => null,
    };

    /// <summary>Makes a portrait size landscape (or leaves a landscape one alone).</summary>
    public static SizeF Landscape(SizeF size) =>
        size.Width >= size.Height ? size : new SizeF(size.Height, size.Width);

    private static SizeF Mm(double width, double height) => new(MmToHundredths(width), MmToHundredths(height));
}
