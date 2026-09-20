using System.Xml.Linq;

namespace PrintPacer.Core.Device;

/// <summary>
/// Reader for HP's LEDM device-management XML, exposed by the printer's embedded web
/// server on plain HTTP. Read-only: this client never writes a setting, never resets the
/// device, and never touches anything outside /DevMgmt/.
///
/// The HP Ink Tank / DeskJet / OfficeJet families all expose this. If the endpoints are
/// absent the probe degrades silently to IPP-only.
///
/// Element names are matched on local name only, because HP varies the namespace prefixes
/// and revisions between firmware versions.
/// </summary>
internal static class HpLedmClient
{
    public sealed record LedmStatus(
        IReadOnlyList<string> StatusCategories,
        IReadOnlyList<string> Alerts);

    public sealed record LedmUsage(
        long? PrintAppImpressions,
        long? EngineImpressions,
        long? JamEvents,
        long? MispickEvents);

    public sealed record LedmConfig(string? Model, string? Serial);

    public static async Task<LedmStatus?> GetStatusAsync(HttpClient http, string host, CancellationToken ct)
    {
        var doc = await TryGetXmlAsync(http, $"http://{host}/DevMgmt/ProductStatusDyn.xml", ct).ConfigureAwait(false);
        if (doc is null) return null;

        var categories = Locals(doc, "StatusCategory")
            .Select(e => e.Value.Trim())
            .Where(v => v.Length > 0)
            .ToList();

        // Alerts carry the detail: which tray, which cartridge, which door.
        var alerts = Locals(doc, "Alert")
            .Select(a => Local(a, "AlertDetailsProductStatusAlert")?.Value.Trim()
                         ?? Local(a, "ProductStatusAlertID")?.Value.Trim()
                         ?? Local(a, "StringId")?.Value.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct()
            .ToList();

        return new LedmStatus(categories, alerts);
    }

    public static async Task<LedmUsage?> GetUsageAsync(HttpClient http, string host, CancellationToken ct)
    {
        var doc = await TryGetXmlAsync(http, $"http://{host}/DevMgmt/ProductUsageDyn.xml", ct).ConfigureAwait(false);
        if (doc is null) return null;

        // PrintApplicationSubunit counts pages that arrived as host print jobs.
        // PrinterSubunit counts everything the print engine did, including walk-up copies.
        // We capture both: the first is the precise one, the second is the fallback.
        var printApp = FirstLongUnder(doc, "PrintApplicationSubunit", "TotalImpressions");
        var engine = FirstLongUnder(doc, "PrinterSubunit", "TotalImpressions");
        var jams = FirstLongUnder(doc, "PrinterSubunit", "JamEvents");
        var mispicks = FirstLongUnder(doc, "PrinterSubunit", "MispickEvents");

        if (printApp is null && engine is null) return null;
        return new LedmUsage(printApp, engine, jams, mispicks);
    }

    public static async Task<LedmConfig?> GetConfigAsync(HttpClient http, string host, CancellationToken ct)
    {
        var doc = await TryGetXmlAsync(http, $"http://{host}/DevMgmt/ProductConfigDyn.xml", ct).ConfigureAwait(false);
        if (doc is null) return null;

        return new LedmConfig(
            Locals(doc, "MakeAndModel").FirstOrDefault()?.Value.Trim(),
            Locals(doc, "SerialNumber").FirstOrDefault()?.Value.Trim());
    }

    /// <summary>
    /// Maps HP's status vocabulary onto our device states. Unknown categories are treated
    /// as "not a fault" rather than guessed at, and are still surfaced verbatim in the UI.
    /// </summary>
    public static (DeviceState State, List<string> Reasons) MapStatus(LedmStatus status)
    {
        var reasons = new List<string>();
        var state = DeviceState.Unknown;

        foreach (var raw in status.StatusCategories)
        {
            switch (raw)
            {
                case "ready":
                    if (state == DeviceState.Unknown) state = DeviceState.Idle;
                    break;
                case "processing":
                case "printing":
                    state = DeviceState.Processing;
                    break;
                case "inPowerSave":
                    if (state is DeviceState.Unknown or DeviceState.Idle) state = DeviceState.PowerSave;
                    break;
                case "initializing":
                case "inkSystemInitializing":
                    if (state is DeviceState.Unknown or DeviceState.Idle) state = DeviceState.Processing;
                    reasons.Add(Humanise(raw));
                    break;

                // Anything in this set means paper will not come out until someone intervenes.
                case "outOfPaper":
                case "trayEmpty":
                case "jamInPrinter":
                case "mediaJam":
                case "closeDoorOrCover":
                case "doorOpen":
                case "cartridgeMissing":
                case "cartridgeFailure":
                case "genuineHP":
                case "outputBinFull":
                case "carriageJam":
                case "scannerError":
                case "hardError":
                case "shuttingDown":
                case "printerOffline":
                    state = DeviceState.Stopped;
                    reasons.Add(Humanise(raw));
                    break;

                default:
                    reasons.Add(Humanise(raw));
                    break;
            }
        }

        foreach (var alert in status.Alerts)
            reasons.Add(Humanise(alert));

        return (state, reasons.Distinct().ToList());
    }

    private static string Humanise(string camel)
    {
        if (string.IsNullOrEmpty(camel)) return camel;
        var sb = new System.Text.StringBuilder(camel.Length + 8);
        for (var i = 0; i < camel.Length; i++)
        {
            var c = camel[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(camel[i - 1])) sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }

    private static async Task<XDocument?> TryGetXmlAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return XDocument.Parse(text, LoadOptions.None);
        }
        catch (Exception)
        {
            // Any failure here just means "no LEDM data"; the caller falls back to IPP.
            return null;
        }
    }

    private static IEnumerable<XElement> Locals(XContainer root, string localName) =>
        root.Descendants().Where(e => e.Name.LocalName == localName);

    private static XElement? Local(XContainer root, string localName) =>
        root.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

    private static long? FirstLongUnder(XContainer root, string parentLocalName, string childLocalName)
    {
        var parent = Locals(root, parentLocalName).FirstOrDefault();
        if (parent is null) return null;

        // Direct children only, so that nested UsageByMedia/UsageByJobSize breakdowns
        // (which reuse the same element names) cannot be picked up by mistake.
        var child = parent.Elements().FirstOrDefault(e => e.Name.LocalName == childLocalName);
        return child is not null && long.TryParse(child.Value.Trim(), out var value) ? value : null;
    }
}
