namespace PrintPacer.Core.Device;

/// <summary>What the printer itself says about its state, independent of Windows.</summary>
public enum DeviceState
{
    Unknown = 0,
    Idle = 1,
    Processing = 2,
    /// <summary>Stopped and needing attention: jam, no paper, cover open, cartridge problem.</summary>
    Stopped = 3,
    /// <summary>Asleep / power save. Not a fault; it wakes on the next job.</summary>
    PowerSave = 4,
    /// <summary>Did not answer. Says nothing about whether it is printing.</summary>
    Unreachable = 5,
}

/// <summary>
/// A reading taken directly from the printer over the network.
///
/// This is the second, independent evidence channel. The Windows spooler tells us what
/// Windows did; this tells us what the hardware did. The two together are what make
/// completion verification trustworthy enough to avoid reprinting pages.
/// </summary>
public sealed record DeviceSnapshot
{
    public required bool Reachable { get; init; }
    public DeviceState State { get; init; } = DeviceState.Unknown;

    /// <summary>Normalised reason keywords, e.g. "media-empty", "jam", "cover-open".</summary>
    public IReadOnlyList<string> StateReasons { get; init; } = Array.Empty<string>();

    public string? StateMessage { get; init; }
    public bool? AcceptingJobs { get; init; }

    /// <summary>Jobs the printer itself believes it still has. Null when not reported.</summary>
    public int? QueuedJobCount { get; init; }

    // ---- Physical counters -------------------------------------------------

    /// <summary>
    /// Lifetime impressions attributed to host print jobs. This is the single most
    /// valuable number in the whole application: its delta across a batch is physical,
    /// hardware-level proof of how many sheets actually came out.
    /// </summary>
    public long? PrintAppImpressions { get; init; }

    /// <summary>Lifetime impressions for the whole print engine, including walk-up copies.</summary>
    public long? EngineImpressions { get; init; }

    public long? JamEvents { get; init; }
    public long? MispickEvents { get; init; }

    // ---- Identity ----------------------------------------------------------
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }

    public DateTimeOffset ObservedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Which probe produced this, for the diagnostics pane.</summary>
    public string Source { get; init; } = "none";

    public string? Error { get; init; }

    /// <summary>
    /// The counter used for completion verification. Prefers the print-application counter
    /// because it excludes copies made at the device's own control panel.
    /// </summary>
    public long? VerificationCounter => PrintAppImpressions ?? EngineImpressions;

    public bool HasVerificationCounter => VerificationCounter.HasValue;

    public static DeviceSnapshot Unavailable(string? error = null) => new()
    {
        Reachable = false,
        State = DeviceState.Unreachable,
        Error = error,
        Source = "none",
    };

    public string Describe()
    {
        if (!Reachable) return Error is null ? "Not reachable" : $"Not reachable ({Error})";
        var text = State switch
        {
            DeviceState.Idle => "Ready",
            DeviceState.Processing => "Printing",
            DeviceState.Stopped => "Stopped",
            DeviceState.PowerSave => "Asleep",
            _ => "Unknown",
        };
        if (StateReasons.Count > 0) text += " - " + string.Join(", ", StateReasons);
        return text;
    }
}
