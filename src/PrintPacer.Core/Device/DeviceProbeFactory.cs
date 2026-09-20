using PrintPacer.Core.Engine;
using PrintPacer.Core.Model;
using PrintPacer.Core.Spooler;
using Serilog;

namespace PrintPacer.Core.Device;

/// <summary>
/// Resolves and caches a device probe per printer.
///
/// Address discovery is slow (a WS-Discovery sweep takes a few seconds) and the answer
/// rarely changes, so it is done once per printer and remembered. A failed discovery is
/// also remembered, with a cool-off, so that a printer with no reachable management
/// interface does not cost several seconds on every readiness check.
/// </summary>
public sealed class DeviceProbeFactory : IDeviceProbeFactory, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DeviceProbeFactory>();
    private static readonly TimeSpan FailedDiscoveryCooloff = TimeSpan.FromMinutes(15);

    private readonly ISpooler _spooler;
    private readonly Func<AppSettings> _settings;
    private readonly Dictionary<string, IDeviceProbe> _probes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _failedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DeviceProbeFactory(ISpooler spooler, Func<AppSettings> settings)
    {
        _spooler = spooler;
        _settings = settings;
    }

    public async Task<IDeviceProbe> GetProbeAsync(string printerName, CancellationToken ct)
    {
        var settings = _settings();
        if (!settings.DeviceProbeEnabled) return NullDeviceProbe.Instance;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_probes.TryGetValue(printerName, out var existing)) return existing;

            if (_failedAt.TryGetValue(printerName, out var failed) &&
                DateTimeOffset.UtcNow - failed < FailedDiscoveryCooloff)
            {
                return NullDeviceProbe.Instance;
            }

            // An address typed by the user always wins over discovery.
            var address = string.IsNullOrWhiteSpace(settings.DeviceAddress)
                ? await DiscoverAsync(printerName, ct).ConfigureAwait(false)
                : settings.DeviceAddress!.Trim();

            if (string.IsNullOrWhiteSpace(address))
            {
                _failedAt[printerName] = DateTimeOffset.UtcNow;
                Log.Information("No management address found for {Printer}; " +
                                "completion will be verified from the Windows spooler alone", printerName);
                return NullDeviceProbe.Instance;
            }

            var probe = new NetworkDeviceProbe(address);

            // Confirm it actually answers before adopting it, so a stale or wrong address
            // does not add a multi-second timeout to every readiness check.
            var snapshot = await probe.ProbeAsync(ct).ConfigureAwait(false);
            if (!snapshot.Reachable)
            {
                probe.Dispose();
                _failedAt[printerName] = DateTimeOffset.UtcNow;
                Log.Information("Printer management interface at {Address} did not answer for {Printer}",
                    address, printerName);
                return NullDeviceProbe.Instance;
            }

            Log.Information("Printer {Printer} reachable at {Address} via {Source}; " +
                            "physical page counter is {Available}",
                printerName, address, snapshot.Source,
                snapshot.HasVerificationCounter ? "available" : "not available");

            _probes[printerName] = probe;
            _failedAt.Remove(printerName);
            return probe;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Device probe setup failed for {Printer}", printerName);
            _failedAt[printerName] = DateTimeOffset.UtcNow;
            return NullDeviceProbe.Instance;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> DiscoverAsync(string printerName, CancellationToken ct)
    {
        var snapshot = _spooler.GetPrinter(printerName);
        if (!snapshot.Exists) return null;
        return await DeviceAddressResolver.ResolveAsync(snapshot.PortName, printerName, ct).ConfigureAwait(false);
    }

    public void Invalidate(string printerName)
    {
        _gate.Wait();
        try
        {
            if (_probes.Remove(printerName, out var probe) && probe is IDisposable disposable)
                disposable.Dispose();
            _failedAt.Remove(printerName);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void InvalidateAll()
    {
        _gate.Wait();
        try
        {
            foreach (var probe in _probes.Values.OfType<IDisposable>()) probe.Dispose();
            _probes.Clear();
            _failedAt.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        InvalidateAll();
        _gate.Dispose();
    }
}
