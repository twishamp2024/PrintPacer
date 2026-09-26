using System.Drawing.Printing;
using System.Runtime.InteropServices;
using Serilog;

namespace PrintPacer.Core.Printing;

/// <summary>What a printer's driver does with a request to print in black and white.</summary>
public enum MonochromeSupport
{
    /// <summary>The driver accepted <c>DMCOLOR_MONOCHROME</c> in the job's DEVMODE.</summary>
    Honoured,

    /// <summary>The printer cannot print colour at all, so every job is black and white anyway.</summary>
    MonochromeDevice,

    /// <summary>
    /// The driver's DEVMODE has no colour field, or it put colour back when merging the
    /// request. Windows applications have no other standard way to ask for black ink only.
    /// </summary>
    NotExposed,

    /// <summary>The driver could not be asked: the printer is missing or its driver failed.</summary>
    Unknown,
}

public sealed record MonochromeCheck(MonochromeSupport Support, string? Detail = null)
{
    /// <summary>True when the printer will print black and white because it was asked to.</summary>
    public bool Guaranteed => Support is MonochromeSupport.Honoured or MonochromeSupport.MonochromeDevice;

    /// <summary>What to tell the user when black and white cannot be guaranteed; null when it can.</summary>
    public string? Warning(string printerName) => Support switch
    {
        MonochromeSupport.NotExposed =>
            $"The driver for \"{printerName}\" does not offer a black & white setting to Windows programs, " +
            "so it cannot be told to use black ink only. PrintPacer still sends every page in greyscale, " +
            "but the printer may mix the grey from colour inks. To be sure, choose Greyscale or Black & white " +
            "in the printer's own Printing Preferences.",
        MonochromeSupport.Unknown =>
            $"PrintPacer could not ask the driver for \"{printerName}\" whether it accepts black & white" +
            (Detail is { Length: > 0 } ? $" ({Detail})" : "") +
            ". Pages are still sent in greyscale.",
        _ => null,
    };
}

/// <summary>
/// The printer-driver side of black and white printing.
///
/// A Windows print job's settings travel in its DEVMODE, and the colour setting is the
/// <c>dmColor</c> field: <c>DMCOLOR_MONOCHROME</c> asks the driver to print with black only.
/// <see cref="PrintDocument"/> writes that field from <see cref="PageSettings.Color"/>, but
/// only when the driver declares the field, and it says nothing when it skips it. So rather
/// than trusting the request, <see cref="Check(PrinterSettings, PageSettings)"/> builds the
/// DEVMODE exactly as <see cref="PrintDocument"/> will and reads back what the driver kept.
/// </summary>
public static class PrinterColour
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(PrinterColour));

    internal const uint DM_COLOR = 0x00000800;
    internal const short DMCOLOR_MONOCHROME = 1;
    internal const short DMCOLOR_COLOR = 2;

    // Offsets into DEVMODEW: dmDeviceName (32 WCHARs), four WORDs, dmFields, then the
    // printer half of the union (eight shorts), then dmColor.
    private const int SizeOffset = 68;
    private const int FieldsOffset = 72;
    private const int ColorOffset = 92;

    /// <summary>
    /// Asks the driver whether black and white survives, using the DEVMODE that
    /// <see cref="PrintDocument.Print"/> will create for <paramref name="page"/>, whose
    /// <see cref="PageSettings.Color"/> must already be false.
    /// </summary>
    public static MonochromeCheck Check(PrinterSettings settings, PageSettings page)
    {
        try
        {
            if (!settings.IsValid)
                return new MonochromeCheck(MonochromeSupport.Unknown, "the printer is not available");

            // DC_COLORDEVICE. A monochrome device prints black and white whatever it is told.
            if (!settings.SupportsColor) return new MonochromeCheck(MonochromeSupport.MonochromeDevice);

            // The same call PrintController.OnStartPrint makes to build the device context:
            // the driver's defaults, merged with the page settings through DocumentProperties.
            var handle = settings.GetHdevmode(page);
            try
            {
                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                    return new MonochromeCheck(MonochromeSupport.Unknown, "the driver settings could not be read");

                try
                {
                    return ReadMonochrome(pointer) == true
                        ? new MonochromeCheck(MonochromeSupport.Honoured)
                        : new MonochromeCheck(MonochromeSupport.NotExposed);
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
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not check black and white support on {Printer}", settings.PrinterName);
            return new MonochromeCheck(MonochromeSupport.Unknown, ex.Message);
        }
    }

    /// <summary>Checks a printer by name, as the setup screen does before a job exists.</summary>
    public static MonochromeCheck Check(string printerName)
    {
        var settings = new PrinterSettings { PrinterName = printerName };
        return Check(settings, new PageSettings(settings) { Color = false });
    }

    /// <summary>
    /// Reads the colour setting from a DEVMODE: true for black and white, false for colour,
    /// null when the DEVMODE does not carry a colour setting at all.
    /// </summary>
    internal static bool? ReadMonochrome(IntPtr devMode)
    {
        if (devMode == IntPtr.Zero) return null;

        var size = (ushort)Marshal.ReadInt16(devMode, SizeOffset);
        if (size < ColorOffset + sizeof(short)) return null;

        var fields = (uint)Marshal.ReadInt32(devMode, FieldsOffset);
        if ((fields & DM_COLOR) == 0) return null;

        return Marshal.ReadInt16(devMode, ColorOffset) switch
        {
            DMCOLOR_MONOCHROME => true,
            DMCOLOR_COLOR => false,
            _ => null,
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
