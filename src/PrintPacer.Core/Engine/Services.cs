using PrintPacer.Core.Model;

namespace PrintPacer.Core.Engine;

public enum NotificationKind
{
    Information,
    Success,
    Warning,
    Problem,
}

public sealed record Notification(NotificationKind Kind, string Title, string Message, string? JobId = null);

/// <summary>
/// Surfaces important moments to the user. Implementations must be quiet: a job runs for
/// hours and the user is asleep, so only genuine transitions belong here.
/// </summary>
public interface INotifier
{
    void Notify(Notification notification);
}

public sealed class NullNotifier : INotifier
{
    public static readonly NullNotifier Instance = new();
    public void Notify(Notification notification) { }
}

/// <summary>
/// Keeps the machine awake for the duration of a job.
///
/// Implementations must request only that the SYSTEM stay available; the display is
/// deliberately allowed to sleep, because the point of the feature is to let someone close
/// the lid and go to bed.
/// </summary>
public interface IPowerManager
{
    /// <summary>True while a keep-awake request is currently held.</summary>
    bool IsHoldingRequest { get; }

    void Acquire(string reason);
    void Release();
}

public sealed class NullPowerManager : IPowerManager
{
    public static readonly NullPowerManager Instance = new();
    public bool IsHoldingRequest => false;
    public void Acquire(string reason) { }
    public void Release() { }
}

/// <summary>Supplies a device probe for a given printer, resolving its address on demand.</summary>
public interface IDeviceProbeFactory
{
    Task<Device.IDeviceProbe> GetProbeAsync(string printerName, CancellationToken ct);
    void Invalidate(string printerName);
}

/// <summary>What the UI is shown after a job finishes its recovery analysis at startup.</summary>
public sealed record RecoveryReport(
    string JobId,
    string FileName,
    bool NeedsUserDecision,
    string Summary,
    IReadOnlyList<string> Details)
{
    public static RecoveryReport Clean(PrintJob job, string summary) =>
        new(job.Id, job.FileName, false, summary, Array.Empty<string>());

    /// <summary>
    /// Read out by screen readers for the row as a whole. A record's generated ToString
    /// would announce the type name and every field, including the internal job id, which
    /// tells the listener nothing useful.
    /// </summary>
    public override string ToString() => $"{FileName}. {Summary}";
}
