namespace PrintPacer.Core.Spooler;

/// <summary>Coarse verdict on whether it is safe to submit work to a queue right now.</summary>
public enum PrinterAvailability
{
    /// <summary>We could not determine the state (query failed for a non-fatal reason).</summary>
    Unknown = 0,

    /// <summary>Idle and accepting work.</summary>
    Ready = 1,

    /// <summary>Accepting work, currently busy printing something.</summary>
    Busy = 2,

    /// <summary>The queue itself is paused, or the printer is marked "Use Printer Offline".</summary>
    Paused = 3,

    /// <summary>Windows believes the device is not reachable.</summary>
    Offline = 4,

    /// <summary>Device reports a fault: jam, paper out, door open, needs intervention.</summary>
    Error = 5,

    /// <summary>No queue with this name exists any more.</summary>
    NotFound = 6,
}

/// <summary>A point-in-time reading of a Windows print queue.</summary>
public sealed record PrinterSnapshot
{
    public required string Name { get; init; }
    public required bool Exists { get; init; }
    public PrinterAvailability Availability { get; init; } = PrinterAvailability.Unknown;
    public uint RawStatus { get; init; }
    public uint Attributes { get; init; }
    public string? PortName { get; init; }
    public string? DriverName { get; init; }
    public int QueuedJobCount { get; init; }
    public DateTimeOffset ObservedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Human-readable decoding of <see cref="RawStatus"/>, e.g. "Offline, Paper out".</summary>
    public string StatusText { get; init; } = "Unknown";

    /// <summary>True when the user (or Windows) has ticked "Use Printer Offline" on the queue.</summary>
    public bool WorkOffline { get; init; }

    /// <summary>Set when the query itself failed, as opposed to the printer being in a bad state.</summary>
    public string? QueryError { get; init; }

    public bool CanAcceptWork =>
        Exists && Availability is PrinterAvailability.Ready or PrinterAvailability.Busy;

    public static PrinterSnapshot Missing(string name) => new()
    {
        Name = name,
        Exists = false,
        Availability = PrinterAvailability.NotFound,
        StatusText = "Printer not found",
    };
}

/// <summary>A point-in-time reading of one job in a Windows print queue.</summary>
public sealed record SpoolerJob
{
    public required int JobId { get; init; }
    public string? Document { get; init; }
    public string? UserName { get; init; }
    public string? MachineName { get; init; }
    public uint Status { get; init; }
    public string StatusText { get; init; } = "";

    /// <summary>Driver-reported status string, when the driver supplies one.</summary>
    public string? DriverStatus { get; init; }

    public int TotalPages { get; init; }
    public int PagesPrinted { get; init; }

    /// <summary>
    /// The colour setting in the job's own DEVMODE, which is what the driver renders with:
    /// true for black and white, false for colour, null when the job does not carry one.
    /// </summary>
    public bool? Monochrome { get; init; }

    public int Position { get; init; }
    public long SizeBytes { get; init; }

    /// <summary>
    /// Submission time as reported by the spooler. Advisory only: we identify our jobs by
    /// document token, never by timestamp.
    /// </summary>
    public DateTimeOffset? SubmittedUtc { get; init; }

    public bool IsPrinting => (Status & NativeMethods.JOB_STATUS_PRINTING) != 0;
    public bool IsSpooling => (Status & NativeMethods.JOB_STATUS_SPOOLING) != 0;
    public bool IsPaused => (Status & NativeMethods.JOB_STATUS_PAUSED) != 0;
    public bool IsDeleting => (Status & (NativeMethods.JOB_STATUS_DELETING | NativeMethods.JOB_STATUS_DELETED)) != 0;
    public bool IsCompleted => (Status & (NativeMethods.JOB_STATUS_PRINTED | NativeMethods.JOB_STATUS_COMPLETE)) != 0;
    public bool IsRetained => (Status & NativeMethods.JOB_STATUS_RETAINED) != 0;

    public bool IsErrored => (Status & (NativeMethods.JOB_STATUS_ERROR
                                        | NativeMethods.JOB_STATUS_PAPEROUT
                                        | NativeMethods.JOB_STATUS_USER_INTERVENTION
                                        | NativeMethods.JOB_STATUS_BLOCKED_DEVQ)) != 0;

    public bool IsOffline => (Status & NativeMethods.JOB_STATUS_OFFLINE) != 0;
}
