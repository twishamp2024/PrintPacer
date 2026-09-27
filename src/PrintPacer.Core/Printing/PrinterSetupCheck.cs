using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using PrintPacer.Core.Model;
using Serilog;

namespace PrintPacer.Core.Printing;

/// <summary>One thing about a job's printer settings the user should know, tied to the setting it concerns.</summary>
/// <param name="Setting">The setting on the setup screen it is about: "Sides", "Paper", "Orientation", "Margins" or "Printer".</param>
/// <param name="Blocking">True when the job cannot print as configured; false for advice.</param>
public sealed record SetupIssue(string Setting, bool Blocking, string Message);

/// <summary>What a printer's driver made of a job's sides, paper and orientation.</summary>
public sealed record PrinterSetupReport
{
    public IReadOnlyList<SetupIssue> Issues { get; init; } = Array.Empty<SetupIssue>();

    /// <summary>What the driver declares (DC_DUPLEX). Some drivers turn the paper over by asking a person to do it.</summary>
    public bool CanDuplex { get; init; }

    /// <summary>The paper the job will use, as the driver names it.</summary>
    public string? PaperName { get; init; }

    /// <summary>Portrait size of that paper in hundredths of an inch.</summary>
    public SizeF? PaperSize { get; init; }

    /// <summary>The widest strip at any edge of the paper that the printer cannot mark, in millimetres.</summary>
    public double? UnprintableEdgeMm { get; init; }

    public bool CanPrint => Issues.All(i => !i.Blocking);

    public SetupIssue? FirstBlocking => Issues.FirstOrDefault(i => i.Blocking);
}

/// <summary>
/// The printer-driver side of sides, paper and orientation.
///
/// A Windows print job carries its settings in a DEVMODE: <c>dmDuplex</c> for one or both
/// sides, <c>dmPaperSize</c> and <c>dmOrientation</c>. <see cref="PrintDocument"/> writes those
/// fields from <see cref="PrinterSettings.Duplex"/> and <see cref="PageSettings"/>, but a driver
/// may leave out a field it does not support, or put back its own value when it merges the
/// request - and nothing says so. As with black and white (<see cref="PrinterColour"/>), this
/// therefore builds the DEVMODE exactly as <see cref="PrintDocument.Print"/> will, and reads back
/// what the driver kept. A job is never reported as double-sided because it was asked to be.
///
/// The same code runs on the setup screen, before a job exists, and in the print engine before
/// every batch, so what the user was told and what each batch does cannot drift apart.
/// </summary>
public static class PrinterSetupCheck
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(PrinterSetupCheck));

    // DEVMODEW: dmDeviceName (32 WCHARs), dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra,
    // dmFields, then the printer half of the union and the fields after it.
    private const int SizeOffset = 68;
    private const int FieldsOffset = 72;
    private const int OrientationOffset = 76;
    private const int PaperSizeOffset = 78;
    private const int DuplexOffset = 94;

    internal const uint DM_ORIENTATION = 0x00000001;
    internal const uint DM_PAPERSIZE = 0x00000002;
    internal const uint DM_DUPLEX = 0x00001000;

    internal const short DMORIENT_PORTRAIT = 1;
    internal const short DMORIENT_LANDSCAPE = 2;
    internal const short DMDUP_SIMPLEX = 1;
    internal const short DMDUP_VERTICAL = 2;
    internal const short DMDUP_HORIZONTAL = 3;

    /// <summary>The DEVMODE value each duplex mode is sent as.</summary>
    public static short DevModeValue(DuplexMode mode) => mode switch
    {
        DuplexMode.LongEdge => DMDUP_VERTICAL,
        DuplexMode.ShortEdge => DMDUP_HORIZONTAL,
        _ => DMDUP_SIMPLEX,
    };

    /// <summary>The <see cref="Duplex"/> value that becomes that DEVMODE value.</summary>
    public static Duplex ToDrawingDuplex(DuplexMode mode) => mode switch
    {
        DuplexMode.LongEdge => Duplex.Vertical,
        DuplexMode.ShortEdge => Duplex.Horizontal,
        _ => Duplex.Simplex,
    };

    /// <summary>Reads a DEVMODE's duplex setting back into a mode; null when it carries none.</summary>
    public static DuplexMode? FromDevModeValue(short? value) => value switch
    {
        DMDUP_SIMPLEX => DuplexMode.SingleSided,
        DMDUP_VERTICAL => DuplexMode.LongEdge,
        DMDUP_HORIZONTAL => DuplexMode.ShortEdge,
        _ => null,
    };

    /// <summary>Asks a printer by name, as the setup screen does before a job exists. Never throws.</summary>
    public static PrinterSetupReport Check(string printerName, PrinterSetup setup, double? marginMm = null)
    {
        try
        {
            var settings = new PrinterSettings { PrinterName = printerName };
            var page = new PageSettings(settings);
            return Apply(settings, page, setup, marginMm, measureEdge: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not check print settings on {Printer}", printerName);
            return Unknown(printerName, ex.Message);
        }
    }

    /// <summary>
    /// Puts <paramref name="setup"/> into <paramref name="settings"/> and <paramref name="page"/>
    /// - which <see cref="PrintDocument"/> then prints with - and reports whether the driver kept
    /// it. Blocking issues mean the job must not print: its output would not be what was asked for.
    /// </summary>
    /// <param name="measureEdge">
    /// Also measure the printer's unprintable edge. Always done when a margin is given; a plain
    /// job asks for nothing new of the driver, so its batches skip the extra query.
    /// </param>
    public static PrinterSetupReport Apply(PrinterSettings settings, PageSettings page, PrinterSetup setup,
        double? marginMm = null, bool measureEdge = false)
    {
        var printer = settings.PrinterName;
        var issues = new List<SetupIssue>();

        try
        {
            if (!settings.IsValid)
                return new PrinterSetupReport { Issues = new[] { new SetupIssue("Printer", true, $"The printer \"{printer}\" is not available.") } };

            var canDuplex = settings.CanDuplex;

            // ---- Sides --------------------------------------------------------------------
            if (setup.Duplex != DuplexMode.SingleSided)
            {
                if (!canDuplex)
                {
                    issues.Add(new SetupIssue("Sides", true,
                        $"\"{printer}\" cannot print on both sides of the paper: its driver does not offer double-sided " +
                        "printing. Choose a printer that can, or - for a normal layout - print single-sided."));
                }
                else
                {
                    settings.Duplex = ToDrawingDuplex(setup.Duplex);
                }
            }
            else if (canDuplex)
            {
                // Said explicitly, so a driver whose own default is double-sided cannot turn a
                // single-sided job - batched one page per sheet - into a double-sided one.
                settings.Duplex = Duplex.Simplex;
            }

            // ---- Paper -------------------------------------------------------------------
            PaperSize? paper = null;
            if (setup.Paper != PaperChoice.PrinterDefault)
            {
                paper = FindPaper(settings, setup.Paper);
                if (paper is null)
                {
                    issues.Add(new SetupIssue("Paper", true,
                        $"\"{printer}\" does not offer {setup.Paper.Describe()} paper. Choose another paper size."));
                }
                else
                {
                    page.PaperSize = paper;
                }
            }

            // ---- Orientation ----------------------------------------------------------------
            if (setup.Landscape == true)
            {
                if (settings.LandscapeAngle == 0)
                {
                    issues.Add(new SetupIssue("Orientation", true,
                        $"\"{printer}\" cannot print in landscape, which booklet and brochure sheets need."));
                }
                else
                {
                    page.Landscape = true;
                }
            }

            // ---- What the driver actually kept ----------------------------------------------
            // Nothing to confirm for a plain single-sided job on the default paper: its DEVMODE
            // differs from earlier versions only by saying "simplex" out loud.
            var asksForAnything = setup.Duplex != DuplexMode.SingleSided || paper is not null || setup.Landscape == true;
            if (issues.Count == 0 && asksForAnything) issues.AddRange(ReadBack(settings, page, setup, paper));

            var used = SafePaper(page);
            var edge = marginMm is not null || measureEdge ? UnprintableEdgeMm(page, used) : null;

            if (marginMm is { } margin && edge is { } needed && margin + 0.05 < needed)
            {
                issues.Add(new SetupIssue("Margins", false,
                    $"\"{printer}\" cannot print within {needed:0.#} mm of the edge of the paper, so with a " +
                    $"{margin:0.#} mm margin the outer edge of the pages may be cut off. A margin of at least " +
                    $"{Math.Ceiling(needed):0} mm avoids this."));
            }

            return new PrinterSetupReport
            {
                Issues = issues,
                CanDuplex = canDuplex,
                PaperName = used?.PaperName,
                PaperSize = used is null ? null : new SizeF(used.Width, used.Height),
                UnprintableEdgeMm = edge,
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not apply print settings on {Printer}", printer);
            return Unknown(printer, ex.Message);
        }
    }

    /// <summary>
    /// Builds the DEVMODE <see cref="PrintDocument"/> will create the job with - the driver's
    /// defaults merged with our settings through DocumentProperties - and compares it with the request.
    /// </summary>
    private static IEnumerable<SetupIssue> ReadBack(PrinterSettings settings, PageSettings page,
        PrinterSetup setup, PaperSize? paper)
    {
        var printer = settings.PrinterName;
        var handle = settings.GetHdevmode(page);
        try
        {
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return new[] { new SetupIssue("Printer", setup.Duplex != DuplexMode.SingleSided || setup.Landscape == true,
                    $"The settings of \"{printer}\" could not be read, so PrintPacer cannot confirm the driver accepts them.") };
            }

            try
            {
                var found = new List<SetupIssue>();
                var fields = ReadFields(pointer);

                if (setup.Duplex != DuplexMode.SingleSided)
                {
                    var kept = FromDevModeValue(ReadShort(pointer, DuplexOffset, DM_DUPLEX, fields));
                    if (kept != setup.Duplex)
                    {
                        found.Add(new SetupIssue("Sides", true,
                            $"The driver for \"{printer}\" would not accept \"{setup.Duplex.Describe().ToLowerInvariant()}\"" +
                            (kept is { } other ? $" (it kept \"{other.Describe().ToLowerInvariant()}\")" : "") +
                            ". Nothing will be printed until this is fixed: choose another printer or setting."));
                    }
                }

                if (setup.Landscape == true && ReadShort(pointer, OrientationOffset, DM_ORIENTATION, fields) != DMORIENT_LANDSCAPE)
                {
                    found.Add(new SetupIssue("Orientation", true,
                        $"The driver for \"{printer}\" would not accept landscape orientation."));
                }

                if (paper is not null)
                {
                    var keptPaper = ReadShort(pointer, PaperSizeOffset, DM_PAPERSIZE, fields);
                    if (keptPaper is null)
                    {
                        found.Add(new SetupIssue("Paper", false,
                            $"The driver for \"{printer}\" did not confirm the paper size; check the paper in the tray."));
                    }
                    else if (keptPaper != paper.RawKind)
                    {
                        found.Add(new SetupIssue("Paper", true,
                            $"The driver for \"{printer}\" would not accept {paper.PaperName} paper."));
                    }
                }

                return found;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            GlobalFree(handle);
        }
    }

    /// <summary>The printer's own entry for a paper choice, by kind or, failing that, by size.</summary>
    public static PaperSize? FindPaper(PrinterSettings settings, PaperChoice choice)
    {
        var kind = choice switch
        {
            PaperChoice.A4 => PaperKind.A4,
            PaperChoice.Letter => PaperKind.Letter,
            PaperChoice.Legal => PaperKind.Legal,
            PaperChoice.A3 => PaperKind.A3,
            PaperChoice.A5 => PaperKind.A5,
            PaperChoice.Tabloid => PaperKind.Tabloid,
            _ => (PaperKind?)null,
        };
        if (kind is null) return null;

        var sizes = settings.PaperSizes.Cast<PaperSize>().ToList();
        var byKind = sizes.FirstOrDefault(p => p.Kind == kind);
        if (byKind is not null) return byKind;

        // Some drivers list standard sizes under their own kinds. Match on dimensions, within
        // a millimetre and a half, in either orientation.
        if (ImpositionGeometry.NominalPaper(choice) is not { } nominal) return null;
        var tolerance = ImpositionGeometry.MmToHundredths(1.5);
        return sizes.FirstOrDefault(p =>
            (Math.Abs(p.Width - nominal.Width) <= tolerance && Math.Abs(p.Height - nominal.Height) <= tolerance) ||
            (Math.Abs(p.Width - nominal.Height) <= tolerance && Math.Abs(p.Height - nominal.Width) <= tolerance));
    }

    private static PaperSize? SafePaper(PageSettings page)
    {
        try { return page.PaperSize; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The widest unprintable strip on any edge. <see cref="PageSettings.PrintableArea"/> and the
    /// hard margins are reported in portrait terms whatever the orientation, so they are compared
    /// with the portrait paper size; the largest of the four edges does not depend on orientation.
    /// </summary>
    private static double? UnprintableEdgeMm(PageSettings page, PaperSize? paper)
    {
        try
        {
            if (paper is null) return null;
            var area = page.PrintableArea;
            if (area.Width <= 1 || area.Height <= 1) return null;

            var left = area.X;
            var top = area.Y;
            var right = paper.Width - area.X - area.Width;
            var bottom = paper.Height - area.Y - area.Height;
            var widest = Math.Max(Math.Max(left, top), Math.Max(right, bottom));
            return Math.Max(0, ImpositionGeometry.HundredthsToMm(widest));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static PrinterSetupReport Unknown(string printer, string detail) => new()
    {
        Issues = new[]
        {
            new SetupIssue("Printer", false,
                $"PrintPacer could not ask the driver for \"{printer}\" about sides, paper and orientation ({detail}). " +
                "They will be checked again before each batch is sent."),
        },
    };

    // ---- DEVMODE reading ----------------------------------------------------------------

    private static uint ReadFields(IntPtr devMode) => (uint)Marshal.ReadInt32(devMode, FieldsOffset);

    private static short? ReadShort(IntPtr devMode, int offset, uint field, uint fields)
    {
        var size = (ushort)Marshal.ReadInt16(devMode, SizeOffset);
        if (size < offset + sizeof(short)) return null;
        if ((fields & field) == 0) return null;
        return Marshal.ReadInt16(devMode, offset);
    }

    /// <summary>The duplex setting in a DEVMODE, such as the one Windows stores with each queued job.</summary>
    public static DuplexMode? ReadDuplex(IntPtr devMode)
    {
        if (devMode == IntPtr.Zero) return null;
        return FromDevModeValue(ReadShort(devMode, DuplexOffset, DM_DUPLEX, ReadFields(devMode)));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
