using System.Collections.Concurrent;
using FluentAssertions;
using PrintPacer.Core.Model;
using PrintPacer.Core.Pdf;
using PrintPacer.Core.Printing;
using PrintPacer.Core.Simulation;
using PrintPacer.Core.Spooler;
using Xunit;
using Xunit.Abstractions;

namespace PrintPacer.Tests;

/// <summary>
/// Black and white through the real print path: <see cref="PdfiumPrintEngine"/>, the real
/// printer driver and the real Windows spooler, with the output written to a file instead
/// of paper. Uses the virtual printers that ship with Windows; on a machine without them
/// the tests say so and pass without checking anything.
/// </summary>
public class WindowsPrintPathTests
{
    private const string XpsWriter = "Microsoft XPS Document Writer";
    private const string PrintToPdf = "Microsoft Print to PDF";

    private readonly ITestOutputHelper _output;

    public WindowsPrintPathTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(ColourMode.BlackAndWhite, true)]
    [InlineData(ColourMode.Colour, false)]
    public async Task TheWindowsPrintJobCarriesTheChosenColourSetting(ColourMode mode, bool expectMonochrome)
    {
        if (!Installed(XpsWriter)) return;
        PrinterColour.Check(XpsWriter).Support.Should().Be(MonochromeSupport.Honoured);

        var (result, seen, _) = await PrintAsync(XpsWriter, mode, "xps");

        result.Spooled.Should().BeTrue(result.Error);
        result.ColourWarning.Should().BeNull();
        seen.Should().NotBeEmpty("the job must be visible in the queue while it spools");
        seen.Should().OnlyContain(m => m == expectMonochrome,
            "the DEVMODE Windows stored with the job is what the driver prints with");
    }

    [Fact]
    public async Task ADriverThatRefusesBlackAndWhiteIsReportedAndStillGetsGreyPages()
    {
        if (!Installed(PrintToPdf)) return;

        var check = PrinterColour.Check(PrintToPdf);
        _output.WriteLine($"{PrintToPdf}: {check.Support}");

        var (blackAndWhite, seen, greyPdf) = await PrintAsync(PrintToPdf, ColourMode.BlackAndWhite, "pdf");
        blackAndWhite.Spooled.Should().BeTrue(blackAndWhite.Error);

        if (check.Guaranteed)
        {
            blackAndWhite.ColourWarning.Should().BeNull();
        }
        else
        {
            blackAndWhite.ColourWarning.Should().NotBeNull("the user must not be told the driver was set to black and white");
            seen.Should().NotContain(true);
        }

        // The page content itself is grey either way, and colour jobs are left alone.
        PageColourAnalyzer.Analyse(greyPdf).Should().Equal(false, false, false);

        var (colour, _, colourPdf) = await PrintAsync(PrintToPdf, ColourMode.Colour, "pdf");
        colour.Spooled.Should().BeTrue(colour.Error);
        colour.ColourWarning.Should().BeNull();
        PageColourAnalyzer.Analyse(colourPdf).Should().Equal(false, true, false);
    }

    // ---- Helpers ----------------------------------------------------------------------

    private bool Installed(string printer)
    {
        if (new WindowsSpooler().ListPrinterNames().Contains(printer)) return true;
        _output.WriteLine($"\"{printer}\" is not installed; nothing was checked.");
        return false;
    }

    /// <summary>
    /// Prints pages 1-3 of a fixture whose page 2 is colour, and records the colour setting
    /// of the job in the Windows queue for as long as it is there.
    /// </summary>
    private async Task<(PrintResult Result, IReadOnlyCollection<bool?> Seen, string Output)> PrintAsync(
        string printer, ColourMode mode, string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), "printpacer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "fixture.pdf");
        TestPdfBuilder.Write(source, 3, "Print path fixture", colourPages: new[] { 2 });

        var token = $"PrintPacer TEST {Guid.NewGuid():N}";
        var output = Path.Combine(root, $"printed.{extension}");
        var request = new PrintRequest
        {
            SourcePath = source,
            FirstPage = 1,
            LastPage = 3,
            PrinterName = printer,
            DocumentToken = token,
            RenderDpi = 150,
            ColourMode = mode,
            OutputFilePath = output,
        };

        var seen = new ConcurrentQueue<bool?>();
        using var stop = new CancellationTokenSource();
        var spooler = new WindowsSpooler();
        var watcher = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var job in spooler.ListJobs(printer, out _).Where(j => j.Document == token))
                    seen.Enqueue(job.Monochrome);
                try { await Task.Delay(40, stop.Token); } catch (OperationCanceledException) { break; }
            }
        });

        var result = await new PdfiumPrintEngine().SubmitAsync(request, CancellationToken.None)
            .WaitAsync(TimeSpan.FromMinutes(2));

        // The driver may still be writing the file after spooling ends; keep watching the
        // queue until the output stops growing.
        await WaitForFileToSettleAsync(output);
        stop.Cancel();
        await watcher;

        _output.WriteLine($"{printer}, {mode}: spooled={result.Spooled}, queue saw " +
                          $"[{string.Join(", ", seen.Distinct())}], warning={result.ColourWarning ?? "-"}");
        return (result, seen.ToArray(), output);
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
