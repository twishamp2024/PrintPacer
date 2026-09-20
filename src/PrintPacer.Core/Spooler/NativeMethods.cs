using System.Runtime.InteropServices;

namespace PrintPacer.Core.Spooler;

/// <summary>
/// Raw P/Invoke surface for the Windows print spooler (winspool.drv).
///
/// Everything here is read-only except <see cref="SetJob"/>, which we only ever call
/// with our own job ids. No call in this file requires administrator rights.
/// </summary>
internal static class NativeMethods
{
    private const string Winspool = "winspool.drv";

    // ---- Access rights -----------------------------------------------------
    internal const uint PRINTER_ACCESS_USE = 0x00000008;
    internal const uint STANDARD_RIGHTS_REQUIRED = 0x000F0000;
    internal const uint PRINTER_ACCESS_ADMINISTER = 0x00000004;
    internal const uint PRINTER_ALL_ACCESS =
        STANDARD_RIGHTS_REQUIRED | PRINTER_ACCESS_ADMINISTER | PRINTER_ACCESS_USE;

    // ---- EnumPrinters flags ------------------------------------------------
    internal const uint PRINTER_ENUM_LOCAL = 0x00000002;
    internal const uint PRINTER_ENUM_CONNECTIONS = 0x00000004;

    // ---- Printer status bits (PRINTER_INFO_2.Status) -----------------------
    internal const uint PRINTER_STATUS_PAUSED = 0x00000001;
    internal const uint PRINTER_STATUS_ERROR = 0x00000002;
    internal const uint PRINTER_STATUS_PENDING_DELETION = 0x00000004;
    internal const uint PRINTER_STATUS_PAPER_JAM = 0x00000008;
    internal const uint PRINTER_STATUS_PAPER_OUT = 0x00000010;
    internal const uint PRINTER_STATUS_MANUAL_FEED = 0x00000020;
    internal const uint PRINTER_STATUS_PAPER_PROBLEM = 0x00000040;
    internal const uint PRINTER_STATUS_OFFLINE = 0x00000080;
    internal const uint PRINTER_STATUS_IO_ACTIVE = 0x00000100;
    internal const uint PRINTER_STATUS_BUSY = 0x00000200;
    internal const uint PRINTER_STATUS_PRINTING = 0x00000400;
    internal const uint PRINTER_STATUS_OUTPUT_BIN_FULL = 0x00000800;
    internal const uint PRINTER_STATUS_NOT_AVAILABLE = 0x00001000;
    internal const uint PRINTER_STATUS_WAITING = 0x00002000;
    internal const uint PRINTER_STATUS_PROCESSING = 0x00004000;
    internal const uint PRINTER_STATUS_INITIALIZING = 0x00008000;
    internal const uint PRINTER_STATUS_WARMING_UP = 0x00010000;
    internal const uint PRINTER_STATUS_TONER_LOW = 0x00020000;
    internal const uint PRINTER_STATUS_NO_TONER = 0x00040000;
    internal const uint PRINTER_STATUS_PAGE_PUNT = 0x00080000;
    internal const uint PRINTER_STATUS_USER_INTERVENTION = 0x00100000;
    internal const uint PRINTER_STATUS_OUT_OF_MEMORY = 0x00200000;
    internal const uint PRINTER_STATUS_DOOR_OPEN = 0x00400000;
    internal const uint PRINTER_STATUS_SERVER_UNKNOWN = 0x00800000;
    internal const uint PRINTER_STATUS_POWER_SAVE = 0x01000000;

    // ---- Printer attribute bits (PRINTER_INFO_2.Attributes) ----------------
    internal const uint PRINTER_ATTRIBUTE_QUEUED = 0x00000001;
    internal const uint PRINTER_ATTRIBUTE_DIRECT = 0x00000002;
    internal const uint PRINTER_ATTRIBUTE_WORK_OFFLINE = 0x00000400;

    // ---- Job status bits (JOB_INFO_2.Status) -------------------------------
    internal const uint JOB_STATUS_PAUSED = 0x00000001;
    internal const uint JOB_STATUS_ERROR = 0x00000002;
    internal const uint JOB_STATUS_DELETING = 0x00000004;
    internal const uint JOB_STATUS_SPOOLING = 0x00000008;
    internal const uint JOB_STATUS_PRINTING = 0x00000010;
    internal const uint JOB_STATUS_OFFLINE = 0x00000020;
    internal const uint JOB_STATUS_PAPEROUT = 0x00000040;
    internal const uint JOB_STATUS_PRINTED = 0x00000080;
    internal const uint JOB_STATUS_DELETED = 0x00000100;
    internal const uint JOB_STATUS_BLOCKED_DEVQ = 0x00000200;
    internal const uint JOB_STATUS_USER_INTERVENTION = 0x00000400;
    internal const uint JOB_STATUS_RESTART = 0x00000800;
    internal const uint JOB_STATUS_COMPLETE = 0x00001000;
    internal const uint JOB_STATUS_RETAINED = 0x00002000;
    internal const uint JOB_STATUS_RENDERING_LOCALLY = 0x00004000;

    // ---- SetJob commands ---------------------------------------------------
    internal const uint JOB_CONTROL_PAUSE = 1;
    internal const uint JOB_CONTROL_RESUME = 2;
    internal const uint JOB_CONTROL_CANCEL = 3;
    internal const uint JOB_CONTROL_RESTART = 4;
    internal const uint JOB_CONTROL_DELETE = 5;
    internal const uint JOB_CONTROL_SENT_TO_PRINTER = 6;

    internal const int ERROR_INSUFFICIENT_BUFFER = 122;
    internal const int ERROR_INVALID_PRINTER_NAME = 1801;
    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_ACCESS_DENIED = 5;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PRINTER_DEFAULTS
    {
        public IntPtr pDatatype;
        public IntPtr pDevMode;
        public uint DesiredAccess;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEMTIME
    {
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
    }

    /// <summary>
    /// JOB_INFO_2W. String members are kept as raw pointers and read with
    /// <see cref="Marshal.PtrToStringUni(IntPtr)"/> so that the layout is explicit
    /// and does not depend on marshaller string handling.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct JOB_INFO_2W
    {
        public uint JobId;
        public IntPtr pPrinterName;
        public IntPtr pMachineName;
        public IntPtr pUserName;
        public IntPtr pDocument;
        public IntPtr pNotifyName;
        public IntPtr pDatatype;
        public IntPtr pPrintProcessor;
        public IntPtr pParameters;
        public IntPtr pDriverName;
        public IntPtr pDevMode;
        public IntPtr pStatus;
        public IntPtr pSecurityDescriptor;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint StartTime;
        public uint UntilTime;
        public uint TotalPages;
        public uint Size;
        public SYSTEMTIME Submitted;
        public uint Time;
        public uint PagesPrinted;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PRINTER_INFO_2W
    {
        public IntPtr pServerName;
        public IntPtr pPrinterName;
        public IntPtr pShareName;
        public IntPtr pPortName;
        public IntPtr pDriverName;
        public IntPtr pComment;
        public IntPtr pLocation;
        public IntPtr pDevMode;
        public IntPtr pSepFile;
        public IntPtr pPrintProcessor;
        public IntPtr pDatatype;
        public IntPtr pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PRINTER_INFO_4W
    {
        public IntPtr pPrinterName;
        public IntPtr pServerName;
        public uint Attributes;
    }

    [DllImport(Winspool, EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, ref PRINTER_DEFAULTS pDefault);

    [DllImport(Winspool, SetLastError = true)]
    internal static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport(Winspool, EntryPoint = "GetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetPrinter(IntPtr hPrinter, uint dwLevel, IntPtr pPrinter, uint cbBuf, out uint pcbNeeded);

    [DllImport(Winspool, EntryPoint = "EnumJobsW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool EnumJobs(IntPtr hPrinter, uint FirstJob, uint NoJobs, uint Level,
        IntPtr pJob, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [DllImport(Winspool, EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool EnumPrinters(uint Flags, string? Name, uint Level,
        IntPtr pPrinterEnum, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [DllImport(Winspool, EntryPoint = "SetJobW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool SetJob(IntPtr hPrinter, uint JobId, uint Level, IntPtr pJob, uint Command);

    [DllImport(Winspool, EntryPoint = "GetDefaultPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetDefaultPrinter(System.Text.StringBuilder? pszBuffer, ref int pcchBuffer);

    internal static string? ReadString(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p);
}
