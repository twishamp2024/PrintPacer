using Serilog;

namespace PrintPacer.Core.Device;

/// <summary>
/// Combines the two read-only network views of a printer:
///
///   IPP  (RFC 8011)  - standardised state and state-reasons, works on almost any network printer.
///   HP LEDM          - HP-specific, adds the physical impression, jam and mispick counters.
///
/// IPP supplies the state; LEDM enriches it and supplies the counters. If LEDM is absent we
/// still get useful state. If both are absent the snapshot says "unreachable", and the job
/// engine falls back to spooler-only evidence with correspondingly lower confidence.
/// </summary>
public sealed class NetworkDeviceProbe : IDeviceProbe, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<NetworkDeviceProbe>();

    private readonly HttpClient _http;
    private readonly string _host;

    /// <summary>Remembers whether LEDM worked, so we stop hammering endpoints that 404.</summary>
    private bool _ledmAvailable = true;

    public NetworkDeviceProbe(string host, TimeSpan? timeout = null)
    {
        _host = host.Trim();
        _http = new HttpClient
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(6),
        };
        _http.DefaultRequestHeaders.ConnectionClose = true; // printers dislike long-lived keep-alives
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_host);
    public string Description => $"{_host} (IPP{(_ledmAvailable ? " + HP LEDM" : "")})";

    public async Task<DeviceSnapshot> ProbeAsync(CancellationToken ct)
    {
        if (!IsConfigured) return DeviceSnapshot.Unavailable("no address configured");

        IppClient.IppAttributes? ipp = null;
        HpLedmClient.LedmStatus? ledmStatus = null;
        HpLedmClient.LedmUsage? ledmUsage = null;
        HpLedmClient.LedmConfig? ledmConfig = null;
        string? error = null;

        try
        {
            ipp = await IppClient.GetPrinterAttributesAsync(_http, _host, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (_ledmAvailable)
        {
            try
            {
                ledmStatus = await HpLedmClient.GetStatusAsync(_http, _host, ct).ConfigureAwait(false);
                ledmUsage = await HpLedmClient.GetUsageAsync(_http, _host, ct).ConfigureAwait(false);
                ledmConfig = await HpLedmClient.GetConfigAsync(_http, _host, ct).ConfigureAwait(false);

                if (ledmStatus is null && ledmUsage is null && ledmConfig is null)
                {
                    _ledmAvailable = false;
                    Log.Debug("LEDM endpoints unavailable on {Host}; continuing with IPP only", _host);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                error ??= ex.Message;
            }
        }

        if (ipp is null && ledmStatus is null && ledmUsage is null)
            return DeviceSnapshot.Unavailable(error ?? "no response");

        // ---- Merge the two views -------------------------------------------
        var reasons = new List<string>();
        var state = DeviceState.Unknown;
        var sources = new List<string>();

        if (ipp is not null)
        {
            sources.Add("IPP");
            state = IppClient.MapState(ipp.PrinterState, ipp.StateReasons);
            reasons.AddRange(ipp.StateReasons);
        }

        if (ledmStatus is not null)
        {
            sources.Add("HP LEDM");
            var (ledmState, ledmReasons) = HpLedmClient.MapStatus(ledmStatus);

            // A fault reported by either channel is a fault. We never let a cheerful
            // "idle" from one source overrule a "stopped" from the other.
            if (ledmState == DeviceState.Stopped || state == DeviceState.Unknown)
                state = ledmState;
            else if (state != DeviceState.Stopped && ledmState != DeviceState.Unknown)
                state = ledmState == DeviceState.Processing ? DeviceState.Processing : state;

            reasons.AddRange(ledmReasons);
        }

        return new DeviceSnapshot
        {
            Reachable = true,
            State = state,
            StateReasons = reasons.Where(r => !string.IsNullOrWhiteSpace(r) && r != "Ready")
                                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            StateMessage = ipp?.StateMessage,
            AcceptingJobs = ipp?.AcceptingJobs,
            QueuedJobCount = ipp?.QueuedJobCount,
            PrintAppImpressions = ledmUsage?.PrintAppImpressions,
            EngineImpressions = ledmUsage?.EngineImpressions,
            JamEvents = ledmUsage?.JamEvents,
            MispickEvents = ledmUsage?.MispickEvents,
            Model = ledmConfig?.Model ?? ipp?.MakeAndModel,
            SerialNumber = ledmConfig?.Serial,
            Source = sources.Count == 0 ? "none" : string.Join(" + ", sources),
        };
    }

    public void Dispose() => _http.Dispose();
}
