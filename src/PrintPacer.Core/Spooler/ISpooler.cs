namespace PrintPacer.Core.Spooler;

/// <summary>
/// Read-mostly view of the Windows print spooler. Abstracted so the job engine can be
/// tested exhaustively against a simulated spooler, including failure modes that are
/// impractical to reproduce with real hardware.
/// </summary>
public interface ISpooler
{
    /// <summary>Names of all print queues visible to the current user.</summary>
    IReadOnlyList<string> ListPrinterNames();

    /// <summary>
    /// The user's default printer, which is the only sensible thing to pre-select. Null if
    /// Windows has no default set.
    /// </summary>
    string? GetDefaultPrinterName();

    /// <summary>Current state of one queue. Never throws; failures surface as a snapshot.</summary>
    PrinterSnapshot GetPrinter(string printerName);

    /// <summary>
    /// All jobs currently in a queue. Returns an empty list if the queue cannot be opened,
    /// which callers must NOT interpret as "our job finished".
    /// </summary>
    IReadOnlyList<SpoolerJob> ListJobs(string printerName, out bool querySucceeded);

    /// <summary>Asks the spooler to cancel one job. Used only for jobs this application submitted.</summary>
    bool CancelJob(string printerName, int jobId, out string? error);
}
