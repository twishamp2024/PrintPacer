namespace PrintPacer.Core.Device;

/// <summary>
/// Reads the printer's own view of itself over the network. Optional: the application is
/// fully functional without it, just less certain about completion.
/// </summary>
public interface IDeviceProbe
{
    /// <summary>True when an address is configured and the probe is enabled.</summary>
    bool IsConfigured { get; }

    /// <summary>Human-readable description of what this probe is talking to.</summary>
    string Description { get; }

    Task<DeviceSnapshot> ProbeAsync(CancellationToken ct);
}

/// <summary>Used when no device address is known. Always reports "no information".</summary>
public sealed class NullDeviceProbe : IDeviceProbe
{
    public static readonly NullDeviceProbe Instance = new();

    public bool IsConfigured => false;
    public string Description => "No printer address configured";

    public Task<DeviceSnapshot> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(DeviceSnapshot.Unavailable("no address configured"));
}
