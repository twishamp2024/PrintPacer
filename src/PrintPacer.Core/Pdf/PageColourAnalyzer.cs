using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace PrintPacer.Core.Pdf;

/// <summary>
/// Decides, page by page, whether a PDF page will put colour on paper, so that the invoice
/// can charge black-and-white and colour pages at their own rates.
///
/// It looks at what the page actually renders to, not at how the PDF was written. A PDF
/// can declare an RGB colour space for text that is pure black, or embed a "black and white"
/// scan as a colour JPEG; neither costs colour ink. Rendering with PDFium - the same engine
/// that prints the page - and inspecting the pixels answers the question the bill cares
/// about.
///
/// The render is at a deliberately low resolution. Colour is a property of large areas, and
/// at <see cref="AnalysisDpi"/> an A4 page is about 240,000 pixels, so a few hundred pages
/// are checked in seconds.
/// </summary>
public static class PageColourAnalyzer
{
    public const int AnalysisDpi = 50;

    /// <summary>
    /// How far apart a pixel's red, green and blue must be, out of 255, before it counts as
    /// coloured. High enough to ignore JPEG noise and faintly tinted scans of white paper;
    /// low enough that any deliberately coloured ink passes easily.
    /// </summary>
    internal const int ChromaThreshold = 48;

    /// <summary>
    /// A page is colour when at least this share of its pixels are coloured (and at least
    /// <see cref="MinimumColourPixels"/>). A lone stray pixel is noise; a small coloured
    /// logo or a line of blue link text is not.
    /// </summary>
    internal const double MinimumColourFraction = 1.0 / 5000;
    internal const int MinimumColourPixels = 16;

    /// <summary>Classifies every page. Index 0 is page 1.</summary>
    /// <param name="progress">Receives the number of pages checked so far.</param>
    public static bool[] Analyse(string path, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        IDocReader reader;
        int pageCount;

        lock (PdfService.PdfiumLock)
        {
            reader = DocLib.Instance.GetDocReader(path, new PageDimensions(AnalysisDpi / 72.0));
            pageCount = reader.GetPageCount();
        }

        try
        {
            var result = new bool[pageCount];
            for (var index = 0; index < pageCount; index++)
            {
                ct.ThrowIfCancellationRequested();

                byte[] bgra;
                int width, height;

                // The lock is taken per page rather than for the whole document, so that
                // cancelling (the user picked another file) never waits behind a long PDF.
                lock (PdfService.PdfiumLock)
                {
                    using var page = reader.GetPageReader(index);
                    width = page.GetPageWidth();
                    height = page.GetPageHeight();
                    bgra = page.GetImage();
                }

                result[index] = IsColour(bgra, (long)width * height);
                progress?.Report(index + 1);
            }
            return result;
        }
        finally
        {
            lock (PdfService.PdfiumLock) reader.Dispose();
        }
    }

    /// <summary>
    /// True when enough pixels of a straight-alpha BGRA buffer are visibly coloured.
    ///
    /// PDFium leaves unpainted paper fully transparent, and antialiased edges partly so. The
    /// chroma is therefore weighted by alpha: a faint edge pixel of red text contributes
    /// only as much colour as it would actually lay down on white paper.
    /// </summary>
    public static bool IsColour(byte[] bgra, long pixelCount)
    {
        pixelCount = Math.Min(pixelCount, bgra.LongLength / 4);
        if (pixelCount <= 0) return false;

        var needed = Math.Max(MinimumColourPixels, (long)Math.Ceiling(pixelCount * MinimumColourFraction));
        long found = 0;

        for (long i = 0, offset = 0; i < pixelCount; i++, offset += 4)
        {
            int alpha = bgra[offset + 3];
            if (alpha == 0) continue;

            int b = bgra[offset], g = bgra[offset + 1], r = bgra[offset + 2];
            var chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));

            if (chroma * alpha > ChromaThreshold * 255 && ++found >= needed) return true;
        }

        return false;
    }
}
