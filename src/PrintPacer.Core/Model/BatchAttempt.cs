namespace PrintPacer.Core.Model;

/// <summary>
/// A single submission of a batch to the Windows spooler.
///
/// The fields here exist almost entirely to answer one question after an unclean
/// shutdown: did these pages come out of the printer, or not? Everything is
/// captured BEFORE the print call so that the answer survives a crash at any point.
/// </summary>
public sealed class BatchAttempt
{
    public long RowId { get; set; }
    public required string JobId { get; init; }
    public required int BatchNumber { get; init; }
    public required int AttemptNumber { get; init; }

    /// <summary>
    /// Globally unique document name handed to the spooler. This is how we find
    /// our own job among any others after a restart. Never reused across attempts.
    /// </summary>
    public required string DocumentToken { get; init; }

    public AttemptState State { get; set; } = AttemptState.SubmitPending;

    /// <summary>Windows spooler job id, once discovered. Null means we never saw it.</summary>
    public int? SpoolerJobId { get; set; }

    public required string PrinterName { get; init; }

    public DateTimeOffset IntentUtc { get; init; }
    public DateTimeOffset? SubmittedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }

    /// <summary>Highest PagesPrinted value the spooler ever reported for this job.</summary>
    public int SpoolerPagesPrinted { get; set; }

    /// <summary>True once we have observed the job in the queue, i.e. it definitely existed.</summary>
    public bool ObservedInQueue { get; set; }

    /// <summary>True once we have observed the job specifically in a PRINTING state.</summary>
    public bool ObservedPrinting { get; set; }

    // ---- Physical corroboration from the device itself ----------------------

    /// <summary>Printer lifetime impression counter read immediately before submitting. Null if unavailable.</summary>
    public long? DeviceImpressionsBefore { get; set; }

    /// <summary>Printer lifetime impression counter read after the job left the queue.</summary>
    public long? DeviceImpressionsAfter { get; set; }

    public long? DeviceJamEventsBefore { get; set; }
    public long? DeviceJamEventsAfter { get; set; }
    public long? DeviceMispickEventsBefore { get; set; }
    public long? DeviceMispickEventsAfter { get; set; }

    public CompletionEvidence Evidence { get; set; } = CompletionEvidence.None;

    public string? LastSpoolerStatus { get; set; }
    public string? Error { get; set; }

    /// <summary>Human-readable explanation of how we reached the verdict we reached.</summary>
    public string? VerdictExplanation { get; set; }

    public long? DeviceImpressionDelta =>
        DeviceImpressionsBefore is { } b && DeviceImpressionsAfter is { } a ? a - b : null;

    public long? DeviceJamDelta =>
        DeviceJamEventsBefore is { } b && DeviceJamEventsAfter is { } a ? a - b : null;

    public long? DeviceMispickDelta =>
        DeviceMispickEventsBefore is { } b && DeviceMispickEventsAfter is { } a ? a - b : null;
}
