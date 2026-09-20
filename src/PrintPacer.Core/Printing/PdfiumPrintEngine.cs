using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using PrintPacer.Core.Pdf;
using Serilog;

namespace PrintPacer.Core.Printing;

/// <summary>
/// Prints a page range by rasterising it with PDFium and drawing each page onto the
/// Windows printer device context.
///
/// Why this design:
///
/// - One <see cref="PrintDocument.Print"/> call produces exactly one spooler job, which is
///   the core requirement: the printer gets discrete jobs it can finish and rest between,
///   never one enormous continuous job.
///
/// - The source PDF is never rewritten, re-saved or recompressed. We read it and draw it.
///   There is no intermediate PDF to get out of sync, corrupt, or leave behind.
///
/// - PDFium is the same renderer Chrome and Edge use for PDFs, so page fidelity matches
///   what the user sees in their browser.
///
/// - The HP Ink Tank is a PCL-3 raster device with no PostScript or PDF interpreter. Its
///   driver rasterises whatever it is given, so rendering through GDI costs no fidelity
///   that the driver would not have removed anyway.
/// </summary>
public sealed class PdfiumPrintEngine : IPrintEngine
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PdfiumPrintEngine>();

    /// <summary>
    /// Upper bound on render resolution. At 600 dpi a single A4 page is a ~140 MB BGRA
    /// buffer; going higher risks an out-of-memory failure halfway through a batch, which
    /// is far worse than a slightly softer page.
    /// </summary>
    public const int MaxRenderDpi = 600;
    public const int MinRenderDpi = 96;

    public string Name => "PDFium (in-process)";

    public Task<PrintResult> SubmitAsync(PrintRequest request, CancellationToken ct)
    {
        // System.Drawing.Printing blocks for the whole spool, so give it its own thread
        // rather than occupying a thread-pool thread for minutes at a time.
        var tcs = new TaskCompletionSource<PrintResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(Run(request, ct));
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(PrintResult.Failure(ex.Message, DateTimeOffset.UtcNow, beforeAnyOutput: true));
            }
        })
        {
            IsBackground = true,
            Name = $"print-{request.DocumentToken}",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static PrintResult Run(PrintRequest request, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;

        if (request.FirstPage < 1 || request.LastPage < request.FirstPage)
            return PrintResult.Failure($"Invalid page range {request.FirstPage}-{request.LastPage}.", started, true);

        var dpi = Math.Clamp(request.RenderDpi, MinRenderDpi, MaxRenderDpi);
        var scale = dpi / 72.0;

        IDocReader? reader = null;
        var pagesRendered = 0;
        var anythingDrawn = false;
        Exception? renderFailure = null;

        try
        {
            // Validate the printer before touching the document, so a missing printer
            // fails cleanly with nothing spooled.
            var settings = new PrinterSettings { PrinterName = request.PrinterName, Copies = 1 };
            if (!settings.IsValid)
                return PrintResult.Failure($"Printer \"{request.PrinterName}\" is not available.", started, true);

            if (request.OutputFilePath is { Length: > 0 } outputPath)
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (directory is not null) Directory.CreateDirectory(directory);
                settings.PrintToFile = true;
                settings.PrintFileName = Path.GetFullPath(outputPath);
            }

            lock (PdfService.PdfiumLock)
            {
                reader = DocLib.Instance.GetDocReader(request.SourcePath, new PageDimensions(scale));
                var total = reader.GetPageCount();
                if (request.LastPage > total)
                {
                    return PrintResult.Failure(
                        $"Page range {request.FirstPage}-{request.LastPage} exceeds the document's {total} pages.",
                        started, true);
                }
            }

            var flags = RenderFlags.RenderForPrinting | RenderFlags.LimitImageCacheSize;
            if (request.Grayscale) flags |= RenderFlags.Grayscale;

            var nextPage = request.FirstPage;

            using var document = new PrintDocument();
            document.PrinterSettings = settings;
            document.DocumentName = request.DocumentToken;

            // The default controller in some hosting contexts shows a progress dialog.
            // An unattended overnight job must never pop up a window.
            document.PrintController = new StandardPrintController();

            // Keep the graphics origin at the top-left of the printable area so that the
            // fit calculation below is expressed in one consistent coordinate space.
            document.OriginAtMargins = false;
            document.DefaultPageSettings.Color = !request.Grayscale;

            document.PrintPage += (_, e) =>
            {
                if (ct.IsCancellationRequested)
                {
                    e.Cancel = true;
                    return;
                }

                try
                {
                    DrawPage(e, reader!, nextPage - 1, flags, request);
                    anythingDrawn = true;
                    pagesRendered++;
                }
                catch (Exception ex)
                {
                    // Record and stop. Continuing would silently skip a page and shift
                    // every subsequent page of the batch.
                    renderFailure = ex;
                    e.Cancel = true;
                    return;
                }

                nextPage++;
                e.HasMorePages = nextPage <= request.LastPage;
            };

            Log.Information("Spooling {Token}: pages {First}-{Last} to {Printer} at {Dpi} dpi",
                request.DocumentToken, request.FirstPage, request.LastPage, request.PrinterName, dpi);

            document.Print();

            if (renderFailure is not null)
            {
                return PrintResult.Failure(
                    $"Rendering failed on page {nextPage}: {renderFailure.Message}", started, !anythingDrawn);
            }

            if (ct.IsCancellationRequested)
                return PrintResult.Failure("Cancelled before the batch finished spooling.", started, !anythingDrawn);

            if (pagesRendered != request.PageCount)
            {
                return PrintResult.Failure(
                    $"Spooled {pagesRendered} of {request.PageCount} pages.", started, !anythingDrawn);
            }

            return new PrintResult
            {
                Spooled = true,
                PagesRendered = pagesRendered,
                StartedUtc = started,
                FinishedUtc = DateTimeOffset.UtcNow,
            };
        }
        catch (InvalidPrinterException ex)
        {
            return PrintResult.Failure($"Printer unavailable: {ex.Message}", started, !anythingDrawn);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Print submission failed for {Token}", request.DocumentToken);
            return PrintResult.Failure(ex.Message, started, !anythingDrawn);
        }
        finally
        {
            if (reader is not null)
            {
                lock (PdfService.PdfiumLock) reader.Dispose();
            }
        }
    }

    private static void DrawPage(PrintPageEventArgs e, IDocReader reader, int pageIndex,
        RenderFlags flags, PrintRequest request)
    {
        byte[] bgra;
        int pixelWidth, pixelHeight;

        var renderWatch = System.Diagnostics.Stopwatch.StartNew();
        lock (PdfService.PdfiumLock)
        {
            using var page = reader.GetPageReader(pageIndex);
            pixelWidth = page.GetPageWidth();
            pixelHeight = page.GetPageHeight();
            bgra = page.GetImage(flags);
        }
        renderWatch.Stop();

        if (pixelWidth <= 0 || pixelHeight <= 0)
            throw new InvalidOperationException($"Page {pageIndex + 1} reported a zero size.");

        var expected = (long)pixelWidth * pixelHeight * 4;
        if (bgra.LongLength < expected)
            throw new InvalidOperationException(
                $"Page {pageIndex + 1} rendered {bgra.LongLength} bytes, expected {expected}.");

        var graphics = e.Graphics ?? throw new InvalidOperationException("No graphics context for the page.");

        // PrintableArea is in hundredths of an inch and describes the region the hardware
        // can actually mark. Some drivers report it as empty; fall back to the full sheet.
        var printable = e.PageSettings.PrintableArea;
        var availableWidth = printable.Width > 1 ? printable.Width : e.PageBounds.Width;
        var availableHeight = printable.Height > 1 ? printable.Height : e.PageBounds.Height;

        var rotate = request.AutoRotate &&
                     (pixelWidth > pixelHeight) != (availableWidth > availableHeight);

        var sourceWidth = rotate ? pixelHeight : pixelWidth;
        var sourceHeight = rotate ? pixelWidth : pixelHeight;

        RectangleF destination;
        if (request.FitToPrintableArea)
        {
            var scale = Math.Min(availableWidth / sourceWidth, availableHeight / sourceHeight);
            var width = sourceWidth * scale;
            var height = sourceHeight * scale;
            destination = new RectangleF((availableWidth - width) / 2f, (availableHeight - height) / 2f, width, height);
        }
        else
        {
            // Natural size: the render is dpi pixels per inch, the page unit is 1/100 inch.
            var width = sourceWidth * 100f / request.RenderDpi;
            var height = sourceHeight * 100f / request.RenderDpi;
            destination = new RectangleF((availableWidth - width) / 2f, (availableHeight - height) / 2f, width, height);
        }

        // PDFium hands back straight (non-premultiplied) BGRA on a fully transparent
        // background. Leaving the alpha channel for GDI+ to resolve would make the result
        // depend on how each printer driver handles alpha on its device context - and with
        // SourceCopy it would emit black pages, because an unpainted pixel is 0,0,0,0.
        // Flattening onto white here makes the bitmap opaque and the outcome identical on
        // every driver. It is also faster than asking GDI+ to alpha-blend 35 MB per page.
        var flattenWatch = System.Diagnostics.Stopwatch.StartNew();
        FlattenOntoWhite(bgra, (long)pixelWidth * pixelHeight);
        flattenWatch.Stop();

        var drawWatch = System.Diagnostics.Stopwatch.StartNew();
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            // Format32bppRgb tells GDI+ to ignore the (now meaningless) alpha byte.
            using var bitmap = new Bitmap(pixelWidth, pixelHeight, pixelWidth * 4,
                PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());

            // The source is already rasterised at the requested dpi. High-quality bicubic
            // resampling of a 35 MB bitmap costs seconds per page and buys nothing visible
            // on an inkjet, so the cheaper filter is the right trade here.
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.CompositingMode = CompositingMode.SourceCopy;

            if (rotate)
            {
                // Rotate through the transform rather than rotating the bitmap: Bitmap
                // instances wrapping externally pinned memory cannot be rotated in place.
                var state = graphics.Save();
                try
                {
                    graphics.TranslateTransform(destination.X + destination.Width, destination.Y);
                    graphics.RotateTransform(90f);
                    graphics.DrawImage(bitmap, new RectangleF(0, 0, destination.Height, destination.Width));
                }
                finally
                {
                    graphics.Restore(state);
                }
            }
            else
            {
                graphics.DrawImage(bitmap, destination);
            }
        }
        finally
        {
            handle.Free();
        }
        drawWatch.Stop();

        Log.Debug("page {Page}: render {Render} ms, flatten {Flatten} ms, draw {Draw} ms, {W}x{H} px",
            pageIndex + 1, renderWatch.ElapsedMilliseconds, flattenWatch.ElapsedMilliseconds,
            drawWatch.ElapsedMilliseconds, pixelWidth, pixelHeight);
    }

    /// <summary>
    /// Composites straight-alpha BGRA onto an opaque white background, in place.
    ///
    /// The overwhelming majority of pixels on a text page are fully transparent (blank
    /// paper) or fully opaque (ink), so both are handled with a single 32-bit store and
    /// only the small remainder of antialiased edge pixels needs an actual blend.
    /// </summary>
    internal static unsafe void FlattenOntoWhite(byte[] bgra, long pixelCount)
    {
        const uint White = 0xFFFFFFFF;

        fixed (byte* basePointer = bgra)
        {
            var pixels = (uint*)basePointer;
            for (long i = 0; i < pixelCount; i++)
            {
                var value = pixels[i];
                var alpha = value >> 24;

                if (alpha == 0) { pixels[i] = White; continue; }
                if (alpha == 255) { pixels[i] = value | 0xFF000000; continue; }

                var inverse = 255u - alpha;
                var b = ((value & 0xFFu) * alpha + 255u * inverse) / 255u;
                var g = (((value >> 8) & 0xFFu) * alpha + 255u * inverse) / 255u;
                var r = (((value >> 16) & 0xFFu) * alpha + 255u * inverse) / 255u;

                pixels[i] = 0xFF000000u | (r << 16) | (g << 8) | b;
            }
        }
    }
}
