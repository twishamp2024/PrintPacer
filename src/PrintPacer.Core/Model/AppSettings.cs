namespace PrintPacer.Core.Model;

/// <summary>
/// Application-wide preferences. Persisted as a single JSON file next to the database
/// so that a corrupt settings file can never take the job database down with it.
/// </summary>
public sealed class AppSettings
{
    // ---- Defaults for new jobs ---------------------------------------------
    public int DefaultPagesPerBatch { get; set; } = 50;
    public int DefaultDelayMinutes { get; set; } = 60;
    public string? LastPrinterName { get; set; }
    public int RenderDpi { get; set; } = 300;
    public bool Grayscale { get; set; }

    // ---- Monitoring ---------------------------------------------------------
    /// <summary>How often the engine polls the spooler while a batch is in flight.</summary>
    public int SpoolerPollSeconds { get; set; } = 2;

    /// <summary>How often the device (IPP / HP LEDM) is polled. Kept gentle to avoid nagging the printer.</summary>
    public int DevicePollSeconds { get; set; } = 20;

    /// <summary>How long to wait between re-checks when the printer is offline or errored.</summary>
    public int RetryIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Maximum automatic retries for a batch we are CERTAIN did not print.
    /// Ambiguous outcomes are never retried automatically regardless of this value.
    /// </summary>
    public int MaxAutoRetries { get; set; } = 3;

    /// <summary>
    /// Give up waiting for a blocked printer after this long and escalate to NeedsAttention.
    /// Zero means never give up.
    /// </summary>
    public int BlockedGiveUpMinutes { get; set; } = 240;

    /// <summary>
    /// How long a submitted job may sit in the queue without any observable progress
    /// before we treat it as stalled. Generous by default: a 50-page inkjet batch is slow.
    /// </summary>
    public int StallTimeoutMinutes { get; set; } = 45;

    // ---- Power ---------------------------------------------------------------
    /// <summary>Hold a system-required power request while a job is active. The display may still sleep.</summary>
    public bool KeepAwakeWhilePrinting { get; set; } = true;

    // ---- Notifications --------------------------------------------------------
    public bool NotificationsEnabled { get; set; } = true;
    public bool NotifyOnBatchComplete { get; set; } = true;
    public bool NotifyOnProblem { get; set; } = true;
    public bool NotifyOnJobComplete { get; set; } = true;

    // ---- Files ----------------------------------------------------------------
    /// <summary>Null means use the per-user application data directory.</summary>
    public string? TempDirectoryOverride { get; set; }
    public bool DeleteTempFilesAfterBatch { get; set; } = true;
    public int LogRetentionDays { get; set; } = 30;

    // ---- Startup / resume -------------------------------------------------------
    public bool LaunchOnWindowsStartup { get; set; }
    public bool StartMinimised { get; set; }

    /// <summary>
    /// When an unfinished job is found at startup: if true the engine may resume on its own,
    /// but ONLY for jobs whose recovery analysis was unambiguous. Anything doubtful still waits
    /// for a human.
    /// </summary>
    public bool AutoResumeUnambiguousJobs { get; set; } = true;

    // ---- Device integration ------------------------------------------------------
    /// <summary>
    /// Optional printer IP/hostname. When set, the app can read the printer's own status and
    /// physical page counter, which massively improves completion verification.
    /// </summary>
    public string? DeviceAddress { get; set; }
    public bool DeviceProbeEnabled { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
