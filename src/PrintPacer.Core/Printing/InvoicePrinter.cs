using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using PrintPacer.Core.Billing;
using PrintPacer.Core.Persistence;
using Serilog;

namespace PrintPacer.Core.Printing;

/// <summary>
/// Prints a job's invoice as a single one-page Windows print job.
///
/// Abstracted, like <see cref="IPrintEngine"/>, so the engine can be tested against a
/// simulated printer that records what it was asked to print.
/// </summary>
public interface IInvoicePrinter
{
    Task<PrintResult> PrintAsync(Invoice invoice, string printerName, CancellationToken ct);
}

/// <summary>Used when no invoice printer is wired in. Refuses rather than pretending to print.</summary>
public sealed class NullInvoicePrinter : IInvoicePrinter
{
    public static readonly NullInvoicePrinter Instance = new();

    public Task<PrintResult> PrintAsync(Invoice invoice, string printerName, CancellationToken ct) =>
        Task.FromResult(PrintResult.Failure("No invoice printer is configured.", DateTimeOffset.UtcNow, true));
}

/// <summary>Draws the invoice with GDI+ and sends it to a Windows printer.</summary>
public sealed class InvoicePrintEngine : IInvoicePrinter
{
    private static readonly ILogger Log = Serilog.Log.ForContext<InvoicePrintEngine>();

    private readonly CultureInfo _culture;

    /// <param name="culture">Formats money and dates. Defaults to the user's Windows region.</param>
    public InvoicePrintEngine(CultureInfo? culture = null) => _culture = culture ?? CultureInfo.CurrentCulture;

    public Task<PrintResult> PrintAsync(Invoice invoice, string printerName, CancellationToken ct)
    {
        // Same reasoning as the batch engine: PrintDocument blocks for the whole spool.
        var tcs = new TaskCompletionSource<PrintResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(Run(invoice, printerName, ct)); }
            catch (Exception ex) { tcs.TrySetResult(PrintResult.Failure(ex.Message, DateTimeOffset.UtcNow, true)); }
        })
        {
            IsBackground = true,
            Name = $"invoice-{invoice.Number}",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private PrintResult Run(Invoice invoice, string printerName, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;

        var settings = new PrinterSettings { PrinterName = printerName, Copies = 1 };
        if (!settings.IsValid)
            return PrintResult.Failure($"Printer \"{printerName}\" is not available.", started, true);

        var drawn = false;
        Exception? failure = null;

        try
        {
            using var document = new PrintDocument();
            document.PrinterSettings = settings;
            document.DocumentName = $"{AppPaths.ProductName} invoice {invoice.Number}";
            document.PrintController = new StandardPrintController();
            document.DefaultPageSettings.Color = false;

            document.PrintPage += (_, e) =>
            {
                if (ct.IsCancellationRequested) { e.Cancel = true; return; }
                try
                {
                    InvoiceLayout.Draw(e.Graphics!, e.MarginBounds, invoice, _culture);
                    drawn = true;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    e.Cancel = true;
                }
                e.HasMorePages = false;
            };

            Log.Information("Printing invoice {Number} to {Printer}", invoice.Number, printerName);
            document.Print();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Invoice {Number} could not be printed", invoice.Number);
            return PrintResult.Failure(ex.Message, started, !drawn);
        }

        if (failure is not null)
            return PrintResult.Failure($"The invoice could not be drawn: {failure.Message}", started, !drawn);
        if (!drawn)
            return PrintResult.Failure("Cancelled before the invoice was sent.", started, true);

        return new PrintResult { Spooled = true, PagesRendered = 1, StartedUtc = started, FinishedUtc = DateTimeOffset.UtcNow };
    }
}

/// <summary>
/// The printed invoice's layout. Kept apart from the printing so that it can be drawn onto a
/// bitmap for tests and previews exactly as it will appear on paper.
///
/// <paramref name="area"/> is in the graphics' page units; fonts are sized in points, so the
/// same call lays out correctly on a printer (hundredths of an inch) or a bitmap.
/// </summary>
public static class InvoiceLayout
{
    public static void Draw(Graphics g, RectangleF area, Invoice invoice, CultureInfo culture)
    {
        using var titleFont = new Font("Segoe UI", 22f, FontStyle.Bold, GraphicsUnit.Point);
        using var headingFont = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
        using var bodyFont = new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var boldFont = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
        using var totalFont = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Point);
        using var smallFont = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var ink = new SolidBrush(Color.Black);
        using var muted = new SolidBrush(Color.FromArgb(90, 90, 90));
        using var rule = new Pen(Color.Black, PointsToUnits(g, 0.75f));
        using var faintRule = new Pen(Color.FromArgb(170, 170, 170), PointsToUnits(g, 0.5f));
        using var right = new StringFormat { Alignment = StringAlignment.Far };
        using var wrap = new StringFormat { Trimming = StringTrimming.EllipsisCharacter };

        string Money(decimal value) => value.ToString("C", culture);
        string When(DateTimeOffset? utc) => utc is { } v ? v.ToLocalTime().ToString("g", culture) : "—";

        var y = area.Top;
        var gap = PointsToUnits(g, 6f);

        // ---- Header ------------------------------------------------------------
        g.DrawString("INVOICE", titleFont, ink, area.Left, y);
        g.DrawString($"No. {invoice.Number}", bodyFont, ink, new RectangleF(area.Left, y, area.Width, 1000f), right);
        g.DrawString(invoice.IssuedUtc.ToLocalTime().ToString("D", culture), bodyFont, muted,
            new RectangleF(area.Left, y + bodyFont.GetHeight(g), area.Width, 1000f), right);
        y += titleFont.GetHeight(g) + gap;

        g.DrawLine(rule, area.Left, y, area.Right, y);
        y += gap * 2;

        // ---- What was printed --------------------------------------------------
        g.DrawString("Printing of", smallFont, muted, area.Left, y);
        y += smallFont.GetHeight(g);
        var nameHeight = headingFont.GetHeight(g) * 2;
        g.DrawString(invoice.FileName, headingFont, ink, new RectangleF(area.Left, y, area.Width, nameHeight), wrap);
        y += Math.Min(nameHeight, g.MeasureString(invoice.FileName, headingFont, (int)area.Width).Height) + gap;

        foreach (var (label, value) in new[]
                 {
                     ("Printer", invoice.PrinterName),
                     ("Started", When(invoice.StartedUtc)),
                     ("Finished", When(invoice.FinishedUtc)),
                     ("Pages printed", $"{invoice.PagesPrinted} of {invoice.DocumentPages}"),
                 })
        {
            g.DrawString(label, bodyFont, muted, area.Left, y);
            g.DrawString(value, bodyFont, ink, area.Left + area.Width * 0.25f, y);
            y += bodyFont.GetHeight(g);
        }
        y += gap * 3;

        // ---- Charges -----------------------------------------------------------
        var columnPages = area.Left + area.Width * 0.62f;
        var columnRate = area.Left + area.Width * 0.80f;
        var columnAmount = area.Right;

        void Row(string description, string pages, string rate, string amount, Font font, Brush brush)
        {
            g.DrawString(description, font, brush, area.Left, y);
            g.DrawString(pages, font, brush, new RectangleF(area.Left, y, columnPages - area.Left, 1000f), right);
            g.DrawString(rate, font, brush, new RectangleF(area.Left, y, columnRate - area.Left, 1000f), right);
            g.DrawString(amount, font, brush, new RectangleF(area.Left, y, columnAmount - area.Left, 1000f), right);
            y += font.GetHeight(g);
        }

        Row("Description", "Pages", "Rate per page", "Amount", boldFont, muted);
        y += gap / 2;
        g.DrawLine(faintRule, area.Left, y, area.Right, y);
        y += gap;

        Row("Black & white pages", $"{invoice.BlackWhitePages}", Money(invoice.BlackWhitePagePrice),
            Money(invoice.BlackWhiteAmount), bodyFont, ink);
        y += gap / 2;
        Row("Colour pages", $"{invoice.ColourPages}", Money(invoice.ColourPagePrice),
            Money(invoice.ColourAmount), bodyFont, ink);

        y += gap;
        g.DrawLine(rule, columnPages - area.Width * 0.2f, y, area.Right, y);
        y += gap;

        g.DrawString("Total", totalFont, ink, new RectangleF(area.Left, y, columnRate - area.Left, 1000f), right);
        g.DrawString(Money(invoice.Total), totalFont, ink, new RectangleF(area.Left, y, columnAmount - area.Left, 1000f), right);
        y += totalFont.GetHeight(g) + gap * 3;

        // ---- How colour was decided -------------------------------------------
        string note;
        if (invoice.PrintedInGreyscale)
            note = "This job was printed in greyscale, so every page is charged at the black & white rate.";
        else if (invoice.ColourPages == 0)
            note = "No page contained colour, so every page is charged at the black & white rate.";
        else
            note = $"Colour pages: {PageColourMap.Shorten(invoice.ColourPageRanges, 400)}.";

        var noteBox = new RectangleF(area.Left, y, area.Width, area.Bottom - y);
        g.DrawString(note, bodyFont, ink, noteBox, wrap);
        y += g.MeasureString(note, bodyFont, (int)area.Width).Height + gap;

        g.DrawString(
            $"A page is charged as colour when it contains visible colour; this was checked automatically " +
            $"from the page content. Generated by {AppPaths.ProductName}.",
            smallFont, muted, new RectangleF(area.Left, y, area.Width, Math.Max(0, area.Bottom - y)), wrap);
    }

    private static float PointsToUnits(Graphics g, float points) =>
        g.PageUnit switch
        {
            GraphicsUnit.Display => points * 100f / 72f,   // printers: hundredths of an inch
            GraphicsUnit.Pixel => points * g.DpiY / 72f,
            GraphicsUnit.Inch => points / 72f,
            GraphicsUnit.Millimeter => points * 25.4f / 72f,
            GraphicsUnit.Document => points * 300f / 72f,
            _ => points,
        };
}
