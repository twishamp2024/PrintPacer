using System.ComponentModel;
using System.Runtime.InteropServices;
using Serilog;

namespace PrintPacer.Core.Spooler;

/// <summary>
/// Concrete <see cref="ISpooler"/> backed by winspool.drv.
///
/// Design rule enforced here: a failed query is reported as a failed query, never as an
/// empty or healthy result. The job engine treats "I could not ask" very differently from
/// "I asked and the answer was no", because conflating the two is how duplicate printing
/// happens.
/// </summary>
public sealed class WindowsSpooler : ISpooler
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsSpooler>();

    public IReadOnlyList<string> ListPrinterNames()
    {
        const uint level = 4; // PRINTER_INFO_4 is the cheap, fast level for name enumeration.
        const uint flags = NativeMethods.PRINTER_ENUM_LOCAL | NativeMethods.PRINTER_ENUM_CONNECTIONS;

        NativeMethods.EnumPrinters(flags, null, level, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return Array.Empty<string>();

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!NativeMethods.EnumPrinters(flags, null, level, buffer, needed, out _, out var count))
            {
                Log.Warning("EnumPrinters failed: {Error}", new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return Array.Empty<string>();
            }

            var results = new List<string>((int)count);
            var stride = Marshal.SizeOf<NativeMethods.PRINTER_INFO_4W>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<NativeMethods.PRINTER_INFO_4W>(buffer + i * stride);
                var name = NativeMethods.ReadString(item.pPrinterName);
                if (!string.IsNullOrWhiteSpace(name)) results.Add(name);
            }
            return results;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public string? GetDefaultPrinterName()
    {
        var length = 0;

        // The first call reports the required buffer length and is expected to fail.
        NativeMethods.GetDefaultPrinter(null, ref length);
        if (length <= 1) return null;

        var buffer = new System.Text.StringBuilder(length);
        if (!NativeMethods.GetDefaultPrinter(buffer, ref length))
        {
            Log.Debug("GetDefaultPrinter failed: {Error}",
                new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return null;
        }

        var name = buffer.ToString();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public PrinterSnapshot GetPrinter(string printerName)
    {
        if (!TryOpen(printerName, out var handle, out var openError))
        {
            // ERROR_INVALID_PRINTER_NAME / ERROR_FILE_NOT_FOUND mean the queue is genuinely gone.
            // Anything else means we could not ask, which is a different and less certain thing.
            var gone = openError is NativeMethods.ERROR_INVALID_PRINTER_NAME or NativeMethods.ERROR_FILE_NOT_FOUND;
            return gone
                ? PrinterSnapshot.Missing(printerName)
                : new PrinterSnapshot
                {
                    Name = printerName,
                    Exists = true,
                    Availability = PrinterAvailability.Unknown,
                    StatusText = "Could not query printer",
                    QueryError = new Win32Exception(openError).Message,
                };
        }

        try
        {
            NativeMethods.GetPrinter(handle, 2, IntPtr.Zero, 0, out var needed);
            if (needed == 0)
            {
                return new PrinterSnapshot
                {
                    Name = printerName,
                    Exists = true,
                    Availability = PrinterAvailability.Unknown,
                    StatusText = "Could not query printer",
                    QueryError = new Win32Exception(Marshal.GetLastWin32Error()).Message,
                };
            }

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!NativeMethods.GetPrinter(handle, 2, buffer, needed, out _))
                {
                    return new PrinterSnapshot
                    {
                        Name = printerName,
                        Exists = true,
                        Availability = PrinterAvailability.Unknown,
                        StatusText = "Could not query printer",
                        QueryError = new Win32Exception(Marshal.GetLastWin32Error()).Message,
                    };
                }

                var info = Marshal.PtrToStructure<NativeMethods.PRINTER_INFO_2W>(buffer);
                var workOffline = (info.Attributes & NativeMethods.PRINTER_ATTRIBUTE_WORK_OFFLINE) != 0;

                return new PrinterSnapshot
                {
                    Name = printerName,
                    Exists = true,
                    RawStatus = info.Status,
                    Attributes = info.Attributes,
                    PortName = NativeMethods.ReadString(info.pPortName),
                    DriverName = NativeMethods.ReadString(info.pDriverName),
                    QueuedJobCount = (int)info.cJobs,
                    WorkOffline = workOffline,
                    Availability = ClassifyPrinter(info.Status, workOffline),
                    StatusText = DescribePrinterStatus(info.Status, workOffline),
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.ClosePrinter(handle);
        }
    }

    public IReadOnlyList<SpoolerJob> ListJobs(string printerName, out bool querySucceeded)
    {
        querySucceeded = false;

        if (!TryOpen(printerName, out var handle, out _))
            return Array.Empty<SpoolerJob>();

        try
        {
            NativeMethods.EnumJobs(handle, 0, uint.MaxValue, 2, IntPtr.Zero, 0, out var needed, out _);
            if (needed == 0)
            {
                // A genuinely empty queue also reports zero bytes needed. Distinguish by
                // checking that the call itself did not fail for another reason.
                var err = Marshal.GetLastWin32Error();
                querySucceeded = err is 0 or NativeMethods.ERROR_INSUFFICIENT_BUFFER;
                return Array.Empty<SpoolerJob>();
            }

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!NativeMethods.EnumJobs(handle, 0, uint.MaxValue, 2, buffer, needed, out _, out var count))
                {
                    Log.Debug("EnumJobs failed for {Printer}: {Error}",
                        printerName, new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    return Array.Empty<SpoolerJob>();
                }

                querySucceeded = true;
                var jobs = new List<SpoolerJob>((int)count);
                var stride = Marshal.SizeOf<NativeMethods.JOB_INFO_2W>();
                for (var i = 0; i < count; i++)
                {
                    var j = Marshal.PtrToStructure<NativeMethods.JOB_INFO_2W>(buffer + i * stride);
                    jobs.Add(new SpoolerJob
                    {
                        JobId = (int)j.JobId,
                        Document = NativeMethods.ReadString(j.pDocument),
                        UserName = NativeMethods.ReadString(j.pUserName),
                        MachineName = NativeMethods.ReadString(j.pMachineName),
                        DriverStatus = NativeMethods.ReadString(j.pStatus),
                        Status = j.Status,
                        StatusText = DescribeJobStatus(j.Status),
                        TotalPages = (int)j.TotalPages,
                        PagesPrinted = (int)j.PagesPrinted,
                        Position = (int)j.Position,
                        SizeBytes = j.Size,
                        SubmittedUtc = ToDateTime(j.Submitted),
                    });
                }
                return jobs;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.ClosePrinter(handle);
        }
    }

    public bool CancelJob(string printerName, int jobId, out string? error)
    {
        error = null;
        if (!TryOpen(printerName, out var handle, out var openError))
        {
            error = new Win32Exception(openError).Message;
            return false;
        }

        try
        {
            if (NativeMethods.SetJob(handle, (uint)jobId, 0, IntPtr.Zero, NativeMethods.JOB_CONTROL_CANCEL))
                return true;

            var err = Marshal.GetLastWin32Error();
            // The job vanishing between our decision and the call is a success for our purposes.
            if (err is NativeMethods.ERROR_FILE_NOT_FOUND or 1168 /* ERROR_NOT_FOUND */)
                return true;

            error = new Win32Exception(err).Message;
            return false;
        }
        finally
        {
            NativeMethods.ClosePrinter(handle);
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static bool TryOpen(string printerName, out IntPtr handle, out int error)
    {
        var defaults = new NativeMethods.PRINTER_DEFAULTS
        {
            pDatatype = IntPtr.Zero,
            pDevMode = IntPtr.Zero,
            DesiredAccess = NativeMethods.PRINTER_ACCESS_USE,
        };

        if (NativeMethods.OpenPrinter(printerName, out handle, ref defaults) && handle != IntPtr.Zero)
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        handle = IntPtr.Zero;
        return false;
    }

    internal static PrinterAvailability ClassifyPrinter(uint status, bool workOffline)
    {
        // Hard faults first: these need a human near the printer.
        const uint faultMask = NativeMethods.PRINTER_STATUS_ERROR
                               | NativeMethods.PRINTER_STATUS_PAPER_JAM
                               | NativeMethods.PRINTER_STATUS_PAPER_OUT
                               | NativeMethods.PRINTER_STATUS_PAPER_PROBLEM
                               | NativeMethods.PRINTER_STATUS_OUTPUT_BIN_FULL
                               | NativeMethods.PRINTER_STATUS_USER_INTERVENTION
                               | NativeMethods.PRINTER_STATUS_DOOR_OPEN
                               | NativeMethods.PRINTER_STATUS_NO_TONER
                               | NativeMethods.PRINTER_STATUS_OUT_OF_MEMORY
                               | NativeMethods.PRINTER_STATUS_PAGE_PUNT;

        if ((status & faultMask) != 0) return PrinterAvailability.Error;

        if ((status & (NativeMethods.PRINTER_STATUS_OFFLINE
                       | NativeMethods.PRINTER_STATUS_NOT_AVAILABLE
                       | NativeMethods.PRINTER_STATUS_SERVER_UNKNOWN)) != 0)
            return PrinterAvailability.Offline;

        // "Use Printer Offline" is a queue setting, not a device fault, but it will silently
        // hold our job forever, so it must block submission just as hard as a real outage.
        if (workOffline) return PrinterAvailability.Paused;
        if ((status & NativeMethods.PRINTER_STATUS_PAUSED) != 0) return PrinterAvailability.Paused;
        if ((status & NativeMethods.PRINTER_STATUS_PENDING_DELETION) != 0) return PrinterAvailability.NotFound;

        if ((status & (NativeMethods.PRINTER_STATUS_PRINTING
                       | NativeMethods.PRINTER_STATUS_PROCESSING
                       | NativeMethods.PRINTER_STATUS_IO_ACTIVE
                       | NativeMethods.PRINTER_STATUS_BUSY)) != 0)
            return PrinterAvailability.Busy;

        // Status 0 is the overwhelmingly common "idle and fine" reading for consumer drivers.
        return PrinterAvailability.Ready;
    }

    internal static string DescribePrinterStatus(uint status, bool workOffline)
    {
        var parts = new List<string>();
        if (workOffline) parts.Add("Set to work offline");
        void Add(uint bit, string label) { if ((status & bit) != 0) parts.Add(label); }

        Add(NativeMethods.PRINTER_STATUS_PAUSED, "Paused");
        Add(NativeMethods.PRINTER_STATUS_ERROR, "Error");
        Add(NativeMethods.PRINTER_STATUS_PENDING_DELETION, "Being deleted");
        Add(NativeMethods.PRINTER_STATUS_PAPER_JAM, "Paper jam");
        Add(NativeMethods.PRINTER_STATUS_PAPER_OUT, "Out of paper");
        Add(NativeMethods.PRINTER_STATUS_MANUAL_FEED, "Manual feed required");
        Add(NativeMethods.PRINTER_STATUS_PAPER_PROBLEM, "Paper problem");
        Add(NativeMethods.PRINTER_STATUS_OFFLINE, "Offline");
        Add(NativeMethods.PRINTER_STATUS_IO_ACTIVE, "Transferring data");
        Add(NativeMethods.PRINTER_STATUS_BUSY, "Busy");
        Add(NativeMethods.PRINTER_STATUS_PRINTING, "Printing");
        Add(NativeMethods.PRINTER_STATUS_OUTPUT_BIN_FULL, "Output bin full");
        Add(NativeMethods.PRINTER_STATUS_NOT_AVAILABLE, "Not available");
        Add(NativeMethods.PRINTER_STATUS_WAITING, "Waiting");
        Add(NativeMethods.PRINTER_STATUS_PROCESSING, "Processing");
        Add(NativeMethods.PRINTER_STATUS_INITIALIZING, "Initialising");
        Add(NativeMethods.PRINTER_STATUS_WARMING_UP, "Warming up");
        Add(NativeMethods.PRINTER_STATUS_TONER_LOW, "Ink/toner low");
        Add(NativeMethods.PRINTER_STATUS_NO_TONER, "Out of ink/toner");
        Add(NativeMethods.PRINTER_STATUS_PAGE_PUNT, "Page too complex");
        Add(NativeMethods.PRINTER_STATUS_USER_INTERVENTION, "Needs attention at the printer");
        Add(NativeMethods.PRINTER_STATUS_OUT_OF_MEMORY, "Out of memory");
        Add(NativeMethods.PRINTER_STATUS_DOOR_OPEN, "Door open");
        Add(NativeMethods.PRINTER_STATUS_SERVER_UNKNOWN, "Server unknown");
        Add(NativeMethods.PRINTER_STATUS_POWER_SAVE, "Power save");

        return parts.Count == 0 ? "Ready" : string.Join(", ", parts);
    }

    internal static string DescribeJobStatus(uint status)
    {
        var parts = new List<string>();
        void Add(uint bit, string label) { if ((status & bit) != 0) parts.Add(label); }

        Add(NativeMethods.JOB_STATUS_PAUSED, "Paused");
        Add(NativeMethods.JOB_STATUS_ERROR, "Error");
        Add(NativeMethods.JOB_STATUS_DELETING, "Deleting");
        Add(NativeMethods.JOB_STATUS_SPOOLING, "Spooling");
        Add(NativeMethods.JOB_STATUS_PRINTING, "Printing");
        Add(NativeMethods.JOB_STATUS_OFFLINE, "Printer offline");
        Add(NativeMethods.JOB_STATUS_PAPEROUT, "Out of paper");
        Add(NativeMethods.JOB_STATUS_PRINTED, "Printed");
        Add(NativeMethods.JOB_STATUS_DELETED, "Deleted");
        Add(NativeMethods.JOB_STATUS_BLOCKED_DEVQ, "Blocked");
        Add(NativeMethods.JOB_STATUS_USER_INTERVENTION, "Needs attention at the printer");
        Add(NativeMethods.JOB_STATUS_RESTART, "Restarting");
        Add(NativeMethods.JOB_STATUS_COMPLETE, "Complete");
        Add(NativeMethods.JOB_STATUS_RETAINED, "Retained");
        Add(NativeMethods.JOB_STATUS_RENDERING_LOCALLY, "Rendering");

        return parts.Count == 0 ? "Queued" : string.Join(", ", parts);
    }

    private static DateTimeOffset? ToDateTime(NativeMethods.SYSTEMTIME st)
    {
        if (st.wYear == 0) return null;
        try
        {
            return new DateTimeOffset(st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond,
                st.wMilliseconds, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
