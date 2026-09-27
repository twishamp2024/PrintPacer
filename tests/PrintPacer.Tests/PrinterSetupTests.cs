using System.Drawing.Printing;
using System.Runtime.InteropServices;
using FluentAssertions;
using PrintPacer.Core.Model;
using PrintPacer.Core.Printing;
using PrintPacer.Core.Spooler;
using Xunit;
using Xunit.Abstractions;

namespace PrintPacer.Tests;

/// <summary>
/// Double-sided printing, paper and orientation as the printer driver sees them. The checks
/// against installed drivers only ask the driver for its settings - they never print - so they
/// are safe to run on a machine with a real printer attached. Drivers that are not installed are
/// reported and skipped.
/// </summary>
public class PrinterSetupTests
{
    private const string PrintToPdf = "Microsoft Print to PDF";

    private readonly ITestOutputHelper _output;

    public PrinterSetupTests(ITestOutputHelper output) => _output = output;

    // ---- What reaches the DEVMODE -----------------------------------------------------------

    [Theory]
    [InlineData(DuplexMode.SingleSided, 1, Duplex.Simplex)]
    [InlineData(DuplexMode.LongEdge, 2, Duplex.Vertical)]     // DMDUP_VERTICAL: long-edge binding
    [InlineData(DuplexMode.ShortEdge, 3, Duplex.Horizontal)]  // DMDUP_HORIZONTAL: short-edge binding
    public void EachSidesChoiceIsTheMatchingWindowsSetting(DuplexMode mode, short devMode, Duplex drawing)
    {
        PrinterSetupCheck.DevModeValue(mode).Should().Be(devMode);
        PrinterSetupCheck.ToDrawingDuplex(mode).Should().Be(drawing);
        ((short)drawing).Should().Be(devMode, "System.Drawing passes the value straight into dmDuplex");
        PrinterSetupCheck.FromDevModeValue(devMode).Should().Be(mode);
    }

    [Theory]
    [InlineData(2, true, DuplexMode.LongEdge)]
    [InlineData(3, true, DuplexMode.ShortEdge)]
    [InlineData(1, true, DuplexMode.SingleSided)]
    [InlineData(3, false, null)]                  // the field is not declared in dmFields
    [InlineData(7, true, null)]                   // a value no driver should write
    public void TheSidesSettingIsReadBackFromAQueuedJobsDevMode(short dmDuplex, bool declared, DuplexMode? expected)
    {
        // A DEVMODEW as Windows stores it with a queued job: dmSize at 68, dmFields at 72,
        // dmDuplex at 94.
        var buffer = Marshal.AllocHGlobal(220);
        try
        {
            for (var i = 0; i < 220; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt16(buffer, 68, 220);
            Marshal.WriteInt32(buffer, 72, declared ? 0x1000 : 0x0800);
            Marshal.WriteInt16(buffer, 94, dmDuplex);

            PrinterSetupCheck.ReadDuplex(buffer).Should().Be(expected);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        PrinterSetupCheck.ReadDuplex(IntPtr.Zero).Should().BeNull();
    }

    // ---- Real drivers, asked but never printed to ------------------------------------------

    [Fact]
    public void APrinterWithoutDuplexIsRefusedWithAMessageNamingTheSetting()
    {
        if (!Installed(PrintToPdf)) return;

        var report = PrinterSetupCheck.Check(PrintToPdf, new PrinterSetup(DuplexMode.LongEdge, PaperChoice.PrinterDefault, null));

        report.CanDuplex.Should().BeFalse();
        report.CanPrint.Should().BeFalse();
        report.FirstBlocking!.Setting.Should().Be("Sides");
        report.FirstBlocking.Message.Should().Contain("cannot print on both sides");

        // The same printer is perfectly usable single-sided.
        PrinterSetupCheck.Check(PrintToPdf, PrinterSetup.Default).CanPrint.Should().BeTrue();
    }

    [Fact]
    public void LandscapeAndPaperAreAcceptedAndReadBackFromTheDriver()
    {
        if (!Installed(PrintToPdf)) return;

        var report = PrinterSetupCheck.Check(PrintToPdf, new PrinterSetup(DuplexMode.SingleSided, PaperChoice.A3, true), 6);

        report.Issues.Should().BeEmpty(string.Join(" / ", report.Issues.Select(i => i.Message)));
        report.PaperName.Should().Contain("A3");
        report.PaperSize!.Value.Width.Should().BeApproximately(1169, 2, "A3 is 297 mm wide in portrait");
    }

    [Fact]
    public void AnyDuplexCapableDriverKeepsBothFlipDirections()
    {
        var printer = FirstDuplexPrinter();
        if (printer is null)
        {
            _output.WriteLine("No installed printer offers double-sided printing; nothing was checked.");
            return;
        }

        foreach (var mode in new[] { DuplexMode.LongEdge, DuplexMode.ShortEdge })
        {
            var report = PrinterSetupCheck.Check(printer, new PrinterSetup(mode, PaperChoice.PrinterDefault, mode == DuplexMode.ShortEdge));
            _output.WriteLine($"{printer}, {mode}: {string.Join(" / ", report.Issues.Select(i => i.Message))}");
            report.CanDuplex.Should().BeTrue();
            report.Issues.Where(i => i.Setting == "Sides").Should().BeEmpty(
                "the driver declares duplex, so it must keep the dmDuplex value it was given");
        }
    }

    [Fact]
    public void APaperTheDriverDoesNotListIsRefused()
    {
        var printer = new WindowsSpooler().ListPrinterNames().FirstOrDefault(name =>
        {
            var settings = new PrinterSettings { PrinterName = name };
            return settings.IsValid && PrinterSetupCheck.FindPaper(settings, PaperChoice.A3) is null;
        });

        if (printer is null)
        {
            _output.WriteLine("Every installed printer offers A3; nothing was checked.");
            return;
        }

        var report = PrinterSetupCheck.Check(printer, new PrinterSetup(DuplexMode.SingleSided, PaperChoice.A3, true));
        report.CanPrint.Should().BeFalse();
        report.FirstBlocking!.Setting.Should().Be("Paper");
        report.FirstBlocking.Message.Should().Contain("does not offer A3");
    }

    [Fact]
    public void AMarginInsideThePrintersUnprintableEdgeIsAWarningNotABlock()
    {
        var printer = new WindowsSpooler().ListPrinterNames().FirstOrDefault(name =>
            PrinterSetupCheck.Check(name, PrinterSetup.Default).UnprintableEdgeMm is > 1);

        if (printer is null)
        {
            _output.WriteLine("No installed printer reports an unprintable edge; nothing was checked.");
            return;
        }

        var report = PrinterSetupCheck.Check(printer, PrinterSetup.Default, marginMm: 0);
        _output.WriteLine($"{printer}: unprintable edge {report.UnprintableEdgeMm:0.0} mm");

        report.Issues.Should().ContainSingle(i => i.Setting == "Margins").Which.Blocking.Should().BeFalse();
        PrinterSetupCheck.Check(printer, PrinterSetup.Default, marginMm: 20).Issues
            .Should().NotContain(i => i.Setting == "Margins");
    }

    [Fact]
    public void AMissingPrinterIsReportedNotThrown()
    {
        var report = PrinterSetupCheck.Check("No Such Printer 7f3a", new PrinterSetup(DuplexMode.LongEdge, PaperChoice.A4, true));
        report.CanPrint.Should().BeFalse();
        report.FirstBlocking!.Setting.Should().Be("Printer");
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private bool Installed(string printer)
    {
        if (new WindowsSpooler().ListPrinterNames().Contains(printer)) return true;
        _output.WriteLine($"\"{printer}\" is not installed; nothing was checked.");
        return false;
    }

    private static string? FirstDuplexPrinter() => new WindowsSpooler().ListPrinterNames()
        .FirstOrDefault(name => new PrinterSettings { PrinterName = name } is { IsValid: true, CanDuplex: true });
}
