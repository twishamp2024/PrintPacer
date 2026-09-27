using System.Text.Json.Serialization;

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

    /// <summary>
    /// The print mode last chosen on the setup screen, offered again for the next job. Only a
    /// starting point: every job has its own mode, picked on the setup screen and shown there.
    /// </summary>
    public ColourMode ColourMode { get; set; } = ColourMode.Colour;

    /// <summary>
    /// The "Render in greyscale" switch that 1.1 and earlier kept in the Settings window. Read
    /// once so that someone who had it on is offered black and white by default; never written.
    /// </summary>
    [JsonPropertyName("grayscale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyGrayscale { get; set; }

    /// <summary>
    /// Paper, margins, scaling, binding, sections and fold from the last booklet or brochure,
    /// offered again so they need not be re-entered. Only the details are remembered: page
    /// selection, layout and sides always start at all pages, normal, single-sided, so a plain
    /// PDF never prints as a booklet because the previous one did.
    /// </summary>
    public PrintSettings LayoutDefaults { get; set; } = PrintSettings.Default;

    // ---- Invoice (remembered from the last job's setup screen) ----------------
    public bool PrintInvoice { get; set; }
    public decimal BlackWhitePagePrice { get; set; }
    public decimal ColourPagePrice { get; set; }

    // ---- Monitoring ---------------------------------------------------------
    /// <summary>
    /// How often the Windows print queue is polled while a batch is in flight. Cheap, so
    /// the default is frequent; the value exists mainly so a misbehaving driver can be
    /// polled more gently.
    /// </summary>
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
    //
    // There is deliberately no temporary-file setting here. The print engine reads page
    // ranges straight out of the source PDF and draws them; it never writes an
    // intermediate file. That removes temporary-file cleanup as a category of bug
    // entirely, rather than making it configurable.
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
