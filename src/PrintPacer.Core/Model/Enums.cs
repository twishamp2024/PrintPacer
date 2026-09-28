namespace PrintPacer.Core.Model;

/// <summary>
/// Job-level state. This is the single authoritative description of "what is the
/// application doing right now" for one print job. Every transition is persisted
/// before it takes effect, so a crash can never lose a transition.
/// </summary>
public enum JobState
{
    /// <summary>Configured but never started. Nothing has been sent anywhere.</summary>
    Draft = 0,

    /// <summary>Started, but the user asked for a future start time.</summary>
    WaitingForStart = 1,

    /// <summary>About to submit a batch: write-ahead intent is being recorded.</summary>
    PreparingBatch = 2,

    /// <summary>A batch has been handed to the Windows spooler and is in flight.</summary>
    PrintingBatch = 3,

    /// <summary>
    /// The spooler job is gone. We are establishing whether the pages physically
    /// came out. This state exists precisely so that "job vanished" is never
    /// silently equated with "job succeeded".
    /// </summary>
    VerifyingBatch = 4,

    /// <summary>Resting the printer between batches. This is the whole point of the app.</summary>
    CoolingDown = 5,

    /// <summary>Printer not reachable. We deliberately do NOT submit while here.</summary>
    BlockedPrinterOffline = 6,

    /// <summary>Printer reachable but reporting an error (jam, out of paper, door open...).</summary>
    BlockedPrinterError = 7,

    /// <summary>User-initiated hold. No new batch will be submitted.</summary>
    Paused = 8,

    /// <summary>
    /// A human must decide. Reached when we cannot establish with confidence whether
    /// a batch printed. We never guess our way out of this state.
    /// </summary>
    NeedsAttention = 9,

    Completed = 10,
    Failed = 11,
    Cancelled = 12,

    /// <summary>
    /// Released to the print queue, waiting for its turn. Nothing has been sent. The engine
    /// starts queued jobs one at a time, in queue order, once no other job has the printer;
    /// until then the job can still be edited, reordered, held back or removed.
    /// </summary>
    Queued = 13,
}

public enum BatchState
{
    Pending = 0,
    InFlight = 1,
    Completed = 2,
    Failed = 3,
    /// <summary>Outcome genuinely unknown. Requires user confirmation before proceeding.</summary>
    NeedsAttention = 4,
    /// <summary>User declared these pages already printed; do not send them.</summary>
    Skipped = 5,
    Cancelled = 6,
}

/// <summary>
/// Lifecycle of a single submission attempt for a single batch.
/// A batch may have more than one attempt (after an explicit, user-sanctioned retry).
/// </summary>
public enum AttemptState
{
    /// <summary>
    /// Write-ahead intent. Persisted and flushed to disk BEFORE the print API is called.
    /// If we crash and find an attempt in this state, we know a job may or may not exist.
    /// </summary>
    SubmitPending = 0,

    /// <summary>Print API returned; the job is (or was) in the Windows queue.</summary>
    Submitted = 1,

    Spooling = 2,
    Printing = 3,

    /// <summary>No longer visible in the queue. Verification pending.</summary>
    LeftQueue = 4,

    /// <summary>Confirmed printed to our satisfaction. Evidence recorded in <see cref="BatchAttempt.Evidence"/>.</summary>
    Verified = 5,

    /// <summary>Confirmed NOT printed (or definitively errored). Safe to retry automatically.</summary>
    Failed = 6,

    /// <summary>We cannot tell. Never auto-retried.</summary>
    Ambiguous = 7,

    Cancelled = 8,
}

/// <summary>
/// What we actually observed. Recorded per attempt so that the UI can explain
/// *why* it believes a batch printed, and so retry decisions are auditable.
/// </summary>
[Flags]
public enum CompletionEvidence
{
    None = 0,

    /// <summary>Spooler reported JOB_STATUS_PRINTED / JOB_STATUS_COMPLETE (only seen when job retention is on).</summary>
    SpoolerReportedPrinted = 1 << 0,

    /// <summary>We watched the job reach PRINTING and then leave the queue normally.</summary>
    SpoolerVanishedAfterPrinting = 1 << 1,

    /// <summary>Spooler's PagesPrinted reached the expected page count.</summary>
    SpoolerPagesPrintedMatched = 1 << 2,

    /// <summary>Printer's own physical impression counter advanced by exactly the expected amount.</summary>
    DeviceCounterMatched = 1 << 3,

    /// <summary>Printer's physical counter advanced, but by less than expected.</summary>
    DeviceCounterPartial = 1 << 4,

    /// <summary>Printer's physical counter did not move at all.</summary>
    DeviceCounterZero = 1 << 5,

    /// <summary>Printer's physical counter moved by MORE than expected (someone else printed too).</summary>
    DeviceCounterOvershoot = 1 << 6,

    /// <summary>Spooler reported an error status on the job.</summary>
    SpoolerReportedError = 1 << 7,

    /// <summary>Job disappeared without ever being observed in a printing state.</summary>
    VanishedUnobserved = 1 << 8,

    /// <summary>A human looked at the paper and told us.</summary>
    UserConfirmedPrinted = 1 << 9,

    /// <summary>A human told us these pages did not come out.</summary>
    UserConfirmedNotPrinted = 1 << 10,

    /// <summary>The device probe was unavailable, so no physical corroboration was possible.</summary>
    DeviceProbeUnavailable = 1 << 11,

    /// <summary>The job was still in the queue when we resumed after a restart, and we re-attached to it.</summary>
    ReattachedAfterRestart = 1 << 12,
}

/// <summary>How the user wants a job to begin.</summary>
public enum StartMode
{
    Immediately = 0,
    AtTime = 1,
}

/// <summary>
/// Whether a job's pages go to the printer in colour or in black and white. Chosen per job
/// on the setup screen and applied to every batch of that job.
///
/// The numeric values are what the job database stores, and match the 0/1 of the older
/// greyscale flag the column held before this type existed.
/// </summary>
public enum ColourMode
{
    Colour = 0,
    BlackAndWhite = 1,
}

public static class ColourModeText
{
    /// <summary>The wording used everywhere the mode is shown: setup screen, dashboard, log, report.</summary>
    public static string Describe(this ColourMode mode) => mode switch
    {
        ColourMode.BlackAndWhite => "Black & white",
        _ => "Colour",
    };
}
