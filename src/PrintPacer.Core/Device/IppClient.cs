using System.Buffers.Binary;
using System.Text;

namespace PrintPacer.Core.Device;

/// <summary>
/// Minimal IPP/1.1 client: enough to issue Get-Printer-Attributes and read the standard
/// printer-state attributes. Deliberately tiny and dependency-free rather than pulling in
/// a full IPP stack for three attributes.
///
/// IPP is an IETF standard (RFC 8011) supported by essentially every network printer made
/// in the last fifteen years, so this path is not HP-specific.
/// </summary>
internal static class IppClient
{
    private const ushort OpGetPrinterAttributes = 0x000B;

    // Delimiter tags
    private const byte TagOperationAttributes = 0x01;
    private const byte TagEndOfAttributes = 0x03;

    // Value tags we care about
    private const byte TagInteger = 0x21;
    private const byte TagBoolean = 0x22;
    private const byte TagEnum = 0x23;
    private const byte TagCharset = 0x47;
    private const byte TagNaturalLanguage = 0x48;
    private const byte TagUri = 0x45;

    public sealed record IppAttributes(
        int? PrinterState,
        IReadOnlyList<string> StateReasons,
        string? StateMessage,
        bool? AcceptingJobs,
        int? QueuedJobCount,
        string? MakeAndModel,
        string? PrinterName);

    public static async Task<IppAttributes?> GetPrinterAttributesAsync(
        HttpClient http, string host, CancellationToken ct)
    {
        var uri = $"ipp://{host}/ipp/print";
        var body = BuildGetPrinterAttributes(uri);

        using var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", "application/ipp");

        using var response = await http.PostAsync($"http://{host}/ipp/print", content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return Parse(bytes);
    }

    private static byte[] BuildGetPrinterAttributes(string printerUri)
    {
        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        header[0] = 2; header[1] = 0;                                   // IPP 2.0
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], OpGetPrinterAttributes);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 1);          // request-id
        ms.Write(header);

        ms.WriteByte(TagOperationAttributes);
        WriteAttribute(ms, TagCharset, "attributes-charset", "utf-8");
        WriteAttribute(ms, TagNaturalLanguage, "attributes-natural-language", "en");
        WriteAttribute(ms, TagUri, "printer-uri", printerUri);
        ms.WriteByte(TagEndOfAttributes);

        return ms.ToArray();
    }

    private static void WriteAttribute(Stream s, byte tag, string name, string value)
    {
        var n = Encoding.UTF8.GetBytes(name);
        var v = Encoding.UTF8.GetBytes(value);
        s.WriteByte(tag);
        WriteU16(s, (ushort)n.Length); s.Write(n);
        WriteU16(s, (ushort)v.Length); s.Write(v);
    }

    private static void WriteU16(Stream s, ushort value)
    {
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)(value & 0xFF));
    }

    /// <summary>
    /// Walks the attribute groups of an IPP response. Additional values of a 1setOf
    /// attribute are encoded with a zero-length name and belong to the previous attribute.
    /// </summary>
    private static IppAttributes? Parse(byte[] data)
    {
        if (data.Length < 9) return null;

        int? printerState = null, queuedJobs = null;
        var reasons = new List<string>();
        string? stateMessage = null, makeAndModel = null, printerName = null;
        bool? accepting = null;

        var pos = 8; // skip version(2) + status(2) + request-id(4)
        string? currentName = null;

        while (pos < data.Length)
        {
            var tag = data[pos++];

            if (tag == TagEndOfAttributes) break;
            if (tag <= 0x05) { currentName = null; continue; } // delimiter group tag

            if (pos + 2 > data.Length) break;
            var nameLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)); pos += 2;
            if (pos + nameLen > data.Length) break;

            if (nameLen > 0)
            {
                currentName = Encoding.UTF8.GetString(data, pos, nameLen);
                pos += nameLen;
            }

            if (pos + 2 > data.Length) break;
            var valueLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)); pos += 2;
            if (pos + valueLen > data.Length) break;
            var valueSpan = data.AsSpan(pos, valueLen);
            pos += valueLen;

            if (currentName is null) continue;

            switch (currentName)
            {
                case "printer-state" when tag is TagEnum or TagInteger && valueLen == 4:
                    printerState = BinaryPrimitives.ReadInt32BigEndian(valueSpan);
                    break;
                case "printer-state-reasons":
                    reasons.Add(Encoding.UTF8.GetString(valueSpan));
                    break;
                case "printer-state-message":
                    stateMessage ??= Encoding.UTF8.GetString(valueSpan);
                    break;
                case "printer-is-accepting-jobs" when tag == TagBoolean && valueLen == 1:
                    accepting = valueSpan[0] != 0;
                    break;
                case "queued-job-count" when tag == TagInteger && valueLen == 4:
                    queuedJobs = BinaryPrimitives.ReadInt32BigEndian(valueSpan);
                    break;
                case "printer-make-and-model":
                    makeAndModel ??= Encoding.UTF8.GetString(valueSpan);
                    break;
                case "printer-name":
                    printerName ??= Encoding.UTF8.GetString(valueSpan);
                    break;
            }
        }

        // "none" is IPP's way of saying there is nothing wrong; it is noise in the UI.
        reasons.RemoveAll(r => string.IsNullOrWhiteSpace(r) || r == "none");

        if (printerState is null && reasons.Count == 0 && makeAndModel is null) return null;

        return new IppAttributes(printerState, reasons, stateMessage, accepting, queuedJobs,
            makeAndModel, printerName);
    }

    /// <summary>RFC 8011 printer-state values.</summary>
    public static DeviceState MapState(int? ippState, IReadOnlyList<string> reasons)
    {
        // A stopped printer is unambiguous. Otherwise trust the reasons over the coarse state,
        // because some firmware reports "idle" while also reporting "media-empty".
        var blocking = reasons.Any(r =>
            r.Contains("jam", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("media-empty", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("media-needed", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("cover-open", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("door-open", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("marker-supply-empty", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("input-tray-missing", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("output-area-full", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("shutdown", StringComparison.OrdinalIgnoreCase));

        if (blocking) return DeviceState.Stopped;

        return ippState switch
        {
            3 => DeviceState.Idle,
            4 => DeviceState.Processing,
            5 => DeviceState.Stopped,
            _ => DeviceState.Unknown,
        };
    }
}
