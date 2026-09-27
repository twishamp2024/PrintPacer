using System.Globalization;
using System.Text;

namespace PrintPacer.Core.Simulation;

/// <summary>
/// Emits small, valid, uncompressed PDFs whose pages are unmistakably identifiable.
///
/// Every page carries its own number in very large type plus a machine-readable marker
/// line, so that after a batch print we can open the output and assert that pages 51-100
/// really are pages 51-100 and that none are missing, repeated or out of order.
///
/// Written by hand against the base-14 Helvetica font so the fixtures depend on nothing:
/// no font embedding, no compression, no external library.
/// </summary>
public static class TestPdfBuilder
{
    private const double PageWidth = 595.276;   // A4 in points
    private const double PageHeight = 841.890;

    /// <param name="colourPages">1-based pages that also carry a solid red block, for the colour check.</param>
    /// <param name="identityBarcode">
    /// Adds a row of black and white cells encoding the page number, which survives being
    /// rasterised, scaled onto a booklet or brochure panel and printed to a file, so a test can
    /// read back exactly which page landed where. See <see cref="DecodeIdentity"/>.
    /// </param>
    public static void Write(string path, int pageCount, string title, IReadOnlyCollection<int>? colourPages = null,
        bool identityBarcode = false)
    {
        if (pageCount < 1) throw new ArgumentOutOfRangeException(nameof(pageCount));

        var objects = new List<byte[]>();

        // Object numbering: 1 = Catalog, 2 = Pages, 3 = Font(Helvetica), 4 = Font(Helvetica-Bold),
        // then for each page: content stream, then page object.
        const int firstPageObject = 5;

        var pageObjectIds = new int[pageCount];
        for (var i = 0; i < pageCount; i++)
            pageObjectIds[i] = firstPageObject + i * 2 + 1;

        var kids = string.Join(" ", pageObjectIds.Select(id => $"{id} 0 R"));

        objects.Add(Latin1($"<< /Type /Catalog /Pages 2 0 R >>"));
        objects.Add(Latin1($"<< /Type /Pages /Count {pageCount} /Kids [{kids}] >>"));
        objects.Add(Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        objects.Add(Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));

        for (var page = 1; page <= pageCount; page++)
        {
            var content = BuildPageContent(page, pageCount, title, colourPages?.Contains(page) == true)
                          + (identityBarcode ? BuildBarcode(page) : "");
            var contentBytes = Latin1(content);
            var streamObject = new List<byte>();
            streamObject.AddRange(Latin1($"<< /Length {contentBytes.Length} >>\nstream\n"));
            streamObject.AddRange(contentBytes);
            streamObject.AddRange(Latin1("\nendstream"));
            objects.Add(streamObject.ToArray());

            var contentId = firstPageObject + (page - 1) * 2;
            objects.Add(Latin1(
                $"<< /Type /Page /Parent 2 0 R " +
                $"/MediaBox [0 0 {F(PageWidth)} {F(PageHeight)}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> " +
                $"/Contents {contentId} 0 R >>"));
        }

        WritePdf(path, objects, title);
    }

    private static string BuildPageContent(int page, int total, string title, bool colour)
    {
        var sb = new StringBuilder();

        // Border, so a clipped or mis-scaled print is obvious on paper.
        sb.Append("2 w 0.2 0.2 0.2 RG\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"20 20 {F(PageWidth - 40)} {F(PageHeight - 40)} re S\n");

        // Big page number, centred.
        var big = $"{page}";
        var bigSize = 180.0;
        var bigWidth = HelveticaBoldWidth(big) * bigSize / 1000.0;
        sb.Append("BT /F2 ").Append(F(bigSize)).Append(" Tf 0 0 0 rg\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"1 0 0 1 {F((PageWidth - bigWidth) / 2)} {F(PageHeight / 2 - 40)} Tm\n");
        sb.Append('(').Append(Escape(big)).Append(") Tj ET\n");

        // Header.
        sb.Append("BT /F1 16 Tf 0 0 0 rg\n");
        sb.Append(CultureInfo.InvariantCulture, $"1 0 0 1 50 {F(PageHeight - 60)} Tm\n");
        sb.Append('(').Append(Escape(title)).Append(") Tj ET\n");

        // Machine-readable marker used by the verification tooling.
        sb.Append("BT /F1 14 Tf 0 0 0 rg\n");
        sb.Append(CultureInfo.InvariantCulture, $"1 0 0 1 50 {F(PageHeight / 2 - 120)} Tm\n");
        sb.Append('(').Append(Escape($"[[PAGE:{page}/{total}]]")).Append(") Tj ET\n");

        // Footer.
        sb.Append("BT /F1 11 Tf 0.25 0.25 0.25 rg\n");
        sb.Append(CultureInfo.InvariantCulture, $"1 0 0 1 50 40 Tm\n");
        sb.Append('(').Append(Escape($"Page {page} of {total}  -  PrintPacer test fixture")).Append(") Tj ET\n");

        // Light-grey diagonals. Their antialiased edges produce partial-alpha pixels whose
        // channel values exceed the alpha only under a straight (non-premultiplied) model,
        // which is what the render check uses to identify PDFium's pixel format.
        sb.Append("0.902 0.902 0.902 RG 3 w\n");
        for (var i = 0; i < 6; i++)
        {
            var x = 80 + i * 70.0;
            sb.Append(CultureInfo.InvariantCulture,
                $"{F(x)} 120 m {F(x + 60)} {F(PageHeight - 120)} l S\n");
        }

        // Corner ticks confirm the whole printable area is reaching the paper.
        foreach (var (x, y) in new[] { (30.0, 30.0), (PageWidth - 60, 30.0), (30.0, PageHeight - 60), (PageWidth - 60, PageHeight - 60) })
            sb.Append(CultureInfo.InvariantCulture, $"0 0 0 rg {F(x)} {F(y)} 30 30 re f\n");

        if (colour)
            sb.Append(CultureInfo.InvariantCulture, $"0.85 0.1 0.1 rg 60 {F(PageHeight - 200)} 160 80 re f\n");

        return sb.ToString();
    }

    // ---- Identity barcode ------------------------------------------------------------------

    /// <summary>Bits after the always-black guard cell: page numbers up to 1023.</summary>
    public const int BarcodeBits = 10;

    private const double BarcodeLeft = 80;
    private const double BarcodeBottom = 170;
    private const double BarcodeCell = 36;
    private const double BarcodePitch = 40;

    private static string BuildBarcode(int page)
    {
        var sb = new StringBuilder("0 0 0 rg\n");
        for (var cell = 0; cell <= BarcodeBits; cell++)
        {
            var dark = cell == 0 || ((page >> (cell - 1)) & 1) == 1;
            if (!dark) continue;
            sb.Append(CultureInfo.InvariantCulture,
                $"{F(BarcodeLeft + cell * BarcodePitch)} {F(BarcodeBottom)} {F(BarcodeCell)} {F(BarcodeCell)} re f\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reads a page number back from a picture of a page drawn with the identity barcode.
    /// <paramref name="isDark"/> is asked about points given as fractions of the page's width and
    /// height from its top-left corner, so the caller only needs to know where the page landed.
    /// Returns null when the guard cell is not dark - no fixture page is there, only blank paper.
    /// </summary>
    public static int? DecodeIdentity(Func<double, double, bool> isDark)
    {
        double X(int cell) => (BarcodeLeft + cell * BarcodePitch + BarcodeCell / 2) / PageWidth;
        var y = (PageHeight - (BarcodeBottom + BarcodeCell / 2)) / PageHeight;

        if (!isDark(X(0), y)) return null;

        var page = 0;
        for (var bit = 0; bit < BarcodeBits; bit++)
            if (isDark(X(bit + 1), y)) page |= 1 << bit;
        return page;
    }

    /// <summary>Natural size of every fixture page, in points.</summary>
    public static (double Width, double Height) PageSizePt => (PageWidth, PageHeight);

    private static void WritePdf(string path, List<byte[]> objects, string title)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var offsets = new long[objects.Count + 1];

        void Emit(byte[] bytes) => stream.Write(bytes, 0, bytes.Length);

        Emit(Latin1("%PDF-1.4\n"));
        Emit(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }); // binary comment marker

        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = stream.Position;
            Emit(Latin1($"{i + 1} 0 obj\n"));
            Emit(objects[i]);
            Emit(Latin1("\nendobj\n"));
        }

        var infoId = objects.Count + 1;
        offsets = offsets.Append(stream.Position).ToArray();
        Emit(Latin1($"{infoId} 0 obj\n"));
        Emit(Latin1($"<< /Title ({Escape(title)}) /Producer (PrintPacer test fixture generator) " +
                    $"/Author (PrintPacer) /CreationDate (D:20260101000000Z) >>"));
        Emit(Latin1("\nendobj\n"));

        var xrefPosition = stream.Position;
        var count = infoId + 1;
        Emit(Latin1($"xref\n0 {count}\n"));
        Emit(Latin1("0000000000 65535 f \n"));
        for (var i = 1; i < count; i++)
            Emit(Latin1($"{offsets[i]:D10} 00000 n \n"));

        Emit(Latin1($"trailer\n<< /Size {count} /Root 1 0 R /Info {infoId} 0 R >>\n"));
        Emit(Latin1($"startxref\n{xrefPosition}\n%%EOF\n"));
    }

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>Widths for the digits in Helvetica-Bold, enough to centre a page number.</summary>
    private static double HelveticaBoldWidth(string text) => text.Sum(c => c is >= '0' and <= '9' ? 556.0 : 556.0);
}
