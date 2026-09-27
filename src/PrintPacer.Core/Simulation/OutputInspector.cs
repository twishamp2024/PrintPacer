using Docnet.Core;
using Docnet.Core.Models;
using PrintPacer.Core.Pdf;

namespace PrintPacer.Core.Simulation;

/// <summary>One page of a printed-to-file PDF, rasterised so a test can look at what is where.</summary>
/// <param name="Luminance">One byte per pixel, 0 black to 255 white, row by row.</param>
public sealed record InspectedPage(int PixelWidth, int PixelHeight, double WidthPt, double HeightPt, byte[] Luminance)
{
    public bool IsLandscape => WidthPt > HeightPt;

    /// <summary>Whether the pixel at a point given in hundredths of an inch from the top-left is dark.</summary>
    public bool IsDarkAt(double xHundredths, double yHundredths)
    {
        var x = (int)Math.Round(xHundredths / 100.0 * PixelWidth / (WidthPt / 72.0));
        var y = (int)Math.Round(yHundredths / 100.0 * PixelHeight / (HeightPt / 72.0));
        if (x < 0 || y < 0 || x >= PixelWidth || y >= PixelHeight) return false;
        return Luminance[y * PixelWidth + x] < 110;
    }
}

/// <summary>
/// Reads back the output of a batch printed to a file (Microsoft Print to PDF), for tests and
/// the probe tool: page sizes, orientation, and a greyscale raster to sample.
/// </summary>
public static class OutputInspector
{
    public static IReadOnlyList<InspectedPage> Render(string pdfPath, double dpi = 100)
    {
        var pages = new List<InspectedPage>();
        lock (PdfService.PdfiumLock)
        {
            using var sizes = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(1.0));
            using var reader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(dpi / 72.0));
            for (var i = 0; i < reader.GetPageCount(); i++)
            {
                double widthPt, heightPt;
                using (var sized = sizes.GetPageReader(i))
                {
                    widthPt = sized.GetPageWidth();
                    heightPt = sized.GetPageHeight();
                }

                using var page = reader.GetPageReader(i);
                var width = page.GetPageWidth();
                var height = page.GetPageHeight();
                var bgra = page.GetImage();

                var luminance = new byte[width * height];
                for (var p = 0; p < luminance.Length; p++)
                {
                    int b = bgra[p * 4], g = bgra[p * 4 + 1], r = bgra[p * 4 + 2], a = bgra[p * 4 + 3];
                    // Unpainted pixels are transparent: paper, so white.
                    var grey = (r * 299 + g * 587 + b * 114) / 1000;
                    luminance[p] = (byte)((grey * a + 255 * (255 - a)) / 255);
                }

                pages.Add(new InspectedPage(width, height, widthPt, heightPt, luminance));
            }
        }
        return pages;
    }
}
