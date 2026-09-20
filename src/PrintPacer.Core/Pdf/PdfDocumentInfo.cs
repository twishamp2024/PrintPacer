namespace PrintPacer.Core.Pdf;

/// <summary>Everything we learn about a PDF at load time.</summary>
public sealed record PdfDocumentInfo
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public required long FileSizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required int PageCount { get; init; }

    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Producer { get; init; }
    public string? Creator { get; init; }
    public string? PdfVersion { get; init; }
    public DateTimeOffset? CreatedUtc { get; init; }

    public bool IsEncrypted { get; init; }

    /// <summary>Width and height of the first page in points, for a size hint in the UI.</summary>
    public double? FirstPageWidthPt { get; init; }
    public double? FirstPageHeightPt { get; init; }

    /// <summary>
    /// Non-fatal problems noticed while inspecting the file, e.g. "metadata unreadable".
    /// The document is still printable; these are shown as advisories.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public string SizeDisplay => FileSizeBytes switch
    {
        < 1024 => $"{FileSizeBytes} B",
        < 1024 * 1024 => $"{FileSizeBytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{FileSizeBytes / (1024.0 * 1024):0.#} MB",
        _ => $"{FileSizeBytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public string? PageSizeDisplay
    {
        get
        {
            if (FirstPageWidthPt is not { } w || FirstPageHeightPt is not { } h) return null;
            var mmW = w * 25.4 / 72.0;
            var mmH = h * 25.4 / 72.0;
            var name = DescribePaper(mmW, mmH);
            return name is null
                ? $"{mmW:0} × {mmH:0} mm"
                : $"{name} ({mmW:0} × {mmH:0} mm)";
        }
    }

    private static string? DescribePaper(double mmW, double mmH)
    {
        // Compare both orientations with a tolerance that absorbs rounding in the producer.
        static bool Near(double a, double b) => Math.Abs(a - b) < 4.0;
        static bool Match(double w, double h, double tw, double th) =>
            (Near(w, tw) && Near(h, th)) || (Near(w, th) && Near(h, tw));

        if (Match(mmW, mmH, 210, 297)) return "A4";
        if (Match(mmW, mmH, 148, 210)) return "A5";
        if (Match(mmW, mmH, 297, 420)) return "A3";
        if (Match(mmW, mmH, 215.9, 279.4)) return "Letter";
        if (Match(mmW, mmH, 215.9, 355.6)) return "Legal";
        if (Match(mmW, mmH, 176, 250)) return "B5";
        return null;
    }
}

/// <summary>Raised when a file cannot be used as a source document at all.</summary>
public sealed class PdfLoadException : Exception
{
    public PdfLoadException(string message, Exception? inner = null) : base(message, inner) { }
}
