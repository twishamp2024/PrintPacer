using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Serilog;

namespace PrintPacer.Core.Device;

/// <summary>
/// Works out the IP address of a printer so the optional device probe can talk to it.
///
/// Three strategies, cheapest first:
///   1. The port name itself, for ports like "IP_192.168.29.167" or a bare address.
///   2. The Standard TCP/IP Port monitor's registry entry, which records HostName/IPAddress.
///   3. WS-Discovery multicast, which is how Windows found a WSD printer in the first place.
///
/// If all three fail the user can simply type the address in Settings; nothing depends on
/// discovery succeeding.
/// </summary>
public static class DeviceAddressResolver
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(DeviceAddressResolver));

    private static readonly Regex IpPattern = new(
        @"\b(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b", RegexOptions.Compiled);

    /// <summary>
    /// Works out how to reach a printer's management interface, cheapest and most
    /// deterministic strategy first.
    /// </summary>
    /// <param name="portName">The Windows port the queue prints through.</param>
    /// <param name="printerName">The queue name, used to read what Windows recorded about the device.</param>
    /// <summary>
    /// Ports that can never lead to a network device: virtual printers, files, and the old
    /// hardware ports. Attempting discovery for these wastes several seconds on a multicast
    /// sweep that cannot possibly find anything relevant.
    /// </summary>
    private static bool IsLocalOnlyPort(string portName) =>
        portName.StartsWith("nul", StringComparison.OrdinalIgnoreCase) ||
        portName.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase) ||
        portName.StartsWith("PORTPROMPT:", StringComparison.OrdinalIgnoreCase) ||
        portName.StartsWith("SHRFAX:", StringComparison.OrdinalIgnoreCase) ||
        portName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase) ||
        portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
        portName.StartsWith("XPSPort:", StringComparison.OrdinalIgnoreCase) ||
        portName.Contains("_x64__", StringComparison.Ordinal);   // Microsoft Store app ports

    public static async Task<string?> ResolveAsync(string? portName, string? printerName, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(portName) && IsLocalOnlyPort(portName))
        {
            Log.Debug("Port {Port} is local-only; skipping network discovery", portName);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(portName))
        {
            if (TryFromPortName(portName, out var direct))
            {
                Log.Debug("Resolved {Port} to {Address} from the port name", portName, direct);
                return direct;
            }

            if (TryFromTcpIpPortRegistry(portName, out var fromRegistry))
            {
                Log.Debug("Resolved {Port} to {Address} from the TCP/IP port monitor", portName, fromRegistry);
                return fromRegistry;
            }
        }

        // A WSD queue carries no address in its port name, but Windows records where it
        // found the device in the printer's own registry key. That is deterministic and
        // always present, unlike a multicast probe.
        byte[]? expectedMac = null;
        if (!string.IsNullOrWhiteSpace(printerName))
        {
            expectedMac = HardwareAddressFor(printerName);

            if (TryFromPrinterLocation(printerName, out var fromLocation))
            {
                Log.Debug("Resolved {Printer} to {Address} from the recorded device location",
                    printerName, fromLocation);
                return fromLocation;
            }
        }

        // Last resort: ask the network.
        var discovered = await ProbeWsDiscoveryAsync(ct).ConfigureAwait(false);
        if (discovered.Count == 0) return null;
        if (discovered.Count == 1) return discovered[0];

        // Several devices answered. If we know which hardware address this printer has, the
        // ambiguity is resolvable: ask the network stack which of the candidates it belongs to.
        if (expectedMac is not null)
        {
            foreach (var candidate in discovered)
            {
                if (!IPAddress.TryParse(candidate, out var ip)) continue;
                if (!ArpTable.SameMac(ArpTable.ResolveMac(ip), expectedMac)) continue;

                Log.Debug("Chose {Address} from {Count} discovered devices by hardware address",
                    candidate, discovered.Count);
                return candidate;
            }
        }

        Log.Information("WS-Discovery found {Count} devices and none could be matched to {Printer}: {Addresses}",
            discovered.Count, printerName, string.Join(", ", discovered));
        return null;
    }

    /// <summary>
    /// The hardware address Windows recorded for a printer, recovered from the link-local
    /// address in its device location.
    /// </summary>
    private static byte[]? HardwareAddressFor(string printerName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Print\Printers\{printerName}");

            if (key?.GetValue("Location") is not string location) return null;

            var match = Regex.Match(location, @"\[([0-9A-Fa-f:]+)(?:%\d+)?\]");
            return match.Success && IPAddress.TryParse(match.Groups[1].Value, out var parsed)
                ? ArpTable.MacFromLinkLocalIPv6(parsed)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>All addresses that answered a WS-Discovery probe, for a "choose your printer" list.</summary>
    public static Task<IReadOnlyList<string>> DiscoverAllAsync(CancellationToken ct) =>
        ProbeWsDiscoveryAsync(ct);

    private static bool TryFromPortName(string portName, out string address)
    {
        address = "";
        // A WSD port name is a GUID and contains no address; skip it explicitly so that
        // the hex of the GUID can never be mistaken for something useful.
        if (portName.StartsWith("WSD-", StringComparison.OrdinalIgnoreCase)) return false;

        var m = IpPattern.Match(portName);
        if (!m.Success) return false;
        if (!IPAddress.TryParse(m.Groups[1].Value, out _)) return false;

        address = m.Groups[1].Value;
        return true;
    }

    /// <summary>
    /// Reads the "Location" value Windows writes under a printer's registry key. For a WSD
    /// device this is the transport address discovery found, for example
    /// <c>http://[fe80::9e7b:efff:feda:95ed%13]:3911/</c>.
    ///
    /// An IPv4 address there is usable directly. An IPv6 link-local address is not, because
    /// these printers serve their management interface on IPv4 only - but the hardware
    /// address is recoverable from it, and Windows' own neighbour cache maps that back to
    /// the IPv4 address the device is using right now.
    /// </summary>
    private static bool TryFromPrinterLocation(string printerName, out string address)
    {
        address = "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Print\Printers\{printerName}");

            if (key?.GetValue("Location") is not string location || location.Length == 0) return false;

            var v4 = IpPattern.Match(location);
            if (v4.Success && IPAddress.TryParse(v4.Groups[1].Value, out _))
            {
                address = v4.Groups[1].Value;
                return true;
            }

            var v6 = Regex.Match(location, @"\[([0-9A-Fa-f:]+)(?:%\d+)?\]");
            if (!v6.Success || !IPAddress.TryParse(v6.Groups[1].Value, out var parsed)) return false;

            var mac = ArpTable.MacFromLinkLocalIPv6(parsed);
            if (mac is null) return false;

            var resolved = ArpTable.FindIPv4(mac);
            if (resolved is null)
            {
                Log.Debug("Recovered hardware address {Mac} for {Printer}, but it is not in the neighbour cache",
                    BitConverter.ToString(mac), printerName);
                return false;
            }

            address = resolved.ToString();
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the recorded location for {Printer}", printerName);
            return false;
        }
    }

    private static bool TryFromTcpIpPortRegistry(string portName, out string address)
    {
        address = "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports\{portName}");
            if (key is null) return false;

            var host = key.GetValue("HostName") as string;
            var ip = key.GetValue("IPAddress") as string;

            var candidate = !string.IsNullOrWhiteSpace(host) ? host : ip;
            if (string.IsNullOrWhiteSpace(candidate)) return false;

            address = candidate.Trim();
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the TCP/IP port registry entry for {Port}", portName);
            return false;
        }
    }

    /// <summary>
    /// Sends a WS-Discovery Probe for print devices and collects the addresses in the
    /// XAddrs of any ProbeMatch. This is the same mechanism Windows itself uses for WSD
    /// printers, so a printer reachable as a WSD queue will answer it.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ProbeWsDiscoveryAsync(CancellationToken ct)
    {
        const string multicastAddress = "239.255.255.250";
        const int multicastPort = 3702;

        var messageId = Guid.NewGuid().ToString("D");
        var probe =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\" " +
            "xmlns:wsa=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
            "xmlns:wsd=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\" " +
            "xmlns:wsdp=\"http://schemas.xmlsoap.org/ws/2006/02/devprof\">" +
            "<soap:Header>" +
            "<wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>" +
            "<wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</wsa:Action>" +
            $"<wsa:MessageID>urn:uuid:{messageId}</wsa:MessageID>" +
            "</soap:Header>" +
            "<soap:Body><wsd:Probe><wsd:Types>wsdp:Device</wsd:Types></wsd:Probe></soap:Body>" +
            "</soap:Envelope>";

        var found = new List<string>();

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var bytes = Encoding.UTF8.GetBytes(probe);
            var endpoint = new IPEndPoint(IPAddress.Parse(multicastAddress), multicastPort);

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromSeconds(5));

            // Multicast is unreliable and a printer in power save can miss the first probe,
            // so send a few, spread across the listening window.
            _ = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 3 && !window.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        await udp.SendAsync(bytes, bytes.Length, endpoint).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromMilliseconds(700), window.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            }, window.Token);

            while (!window.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(result.Buffer);
                if (!text.Contains("ProbeMatch", StringComparison.OrdinalIgnoreCase)) continue;

                // Prefer the responder's own source address: it is always correct, whereas
                // XAddrs can advertise a hostname we cannot resolve.
                var address = result.RemoteEndPoint.Address.ToString();
                if (!found.Contains(address)) found.Add(address);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "WS-Discovery probe failed");
        }

        return found;
    }
}
