using System.Text;
using PrintPacer.Core.Model;

namespace PrintPacer.Core.Billing;

/// <summary>Where a job's invoice is in its life. Persisted with the job.</summary>
public enum InvoiceState
{
    /// <summary>The user did not ask for an invoice.</summary>
    NotRequested = 0,

    /// <summary>Asked for; will print automatically when the job completes.</summary>
    Waiting = 1,

    /// <summary>
    /// Written to disk before the invoice is handed to the printer, exactly like a batch's
    /// submit intent, so that a crash mid-send is remembered as "may have printed".
    /// </summary>
    Sending = 2,

    Printed = 3,

    /// <summary>The printer refused it. Nothing came out; the user can send it again.</summary>
    Failed = 4,

    /// <summary>
    /// The application stopped while the invoice was being sent. It may or may not be in
    /// the tray, so it is never resent without the user asking.
    /// </summary>
    Interrupted = 5,
}

/// <summary>What the user chose on the setup screen, handed to the engine with the job.</summary>
/// <param name="PageColourMap">
/// One character per page, <c>'C'</c> for colour and <c>'B'</c> for black and white; see
/// <see cref="PageColourMap"/>. May be null only when the job prints in black and white, since
/// then every page is black and white whatever it contains.
/// </param>
public sealed record InvoiceRequest(decimal BlackWhitePagePrice, decimal ColourPagePrice, string? PageColourMap);

/// <summary>Compact per-page colour classification, stored as one character per page.</summary>
public static class PageColourMap
{
    public const char Colour = 'C';
    public const char BlackWhite = 'B';

    public static string Encode(IReadOnlyList<bool> isColour)
    {
        var sb = new StringBuilder(isColour.Count);
        foreach (var colour in isColour) sb.Append(colour ? Colour : BlackWhite);
        return sb.ToString();
    }

    /// <summary>1-based page lookup.</summary>
    public static bool IsColour(string map, int page) => map[page - 1] == Colour;

    public static bool IsValid(string? map, int totalPages) =>
        map is not null && map.Length == totalPages && map.All(c => c is Colour or BlackWhite);

    /// <summary>"1, 3–5, 9": the pages as a reader would write them.</summary>
    public static string DescribeRanges(IReadOnlyList<int> pages)
    {
        var parts = new List<string>();
        for (var i = 0; i < pages.Count; i++)
        {
            var start = pages[i];
            while (i + 1 < pages.Count && pages[i + 1] == pages[i] + 1) i++;
            parts.Add(start == pages[i] ? $"{start}" : $"{start}–{pages[i]}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>Cuts a range list at a comma so it fits in <paramref name="max"/> characters.</summary>
    public static string Shorten(string ranges, int max)
    {
        if (ranges.Length <= max) return ranges;
        var cut = ranges.LastIndexOf(',', max);
        return (cut > 0 ? ranges[..cut] : ranges[..max]) + ", …";
    }
}

/// <summary>
/// The bill for one job: every page that printed, charged at the black-and-white or colour
/// rate according to what is actually on it.
///
/// Built from the job's own batch records, so a page is only ever charged if the engine
/// believes it is on paper. Pages from a batch the user confirmed or skipped as already
/// printed count too, matching the "all N pages printed" the job reports on completion.
/// </summary>
public sealed record Invoice
{
    public required string Number { get; init; }
    public required DateTimeOffset IssuedUtc { get; init; }

    public required string FileName { get; init; }
    public required string PrinterName { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; init; }
    public required int DocumentPages { get; init; }

    public required int BlackWhitePages { get; init; }
    public required int ColourPages { get; init; }
    public required decimal BlackWhitePagePrice { get; init; }
    public required decimal ColourPagePrice { get; init; }

    /// <summary>True when the job printed in black and white, so colour content was charged as black and white.</summary>
    public bool PrintedInBlackAndWhite { get; init; }

    /// <summary>The colour pages as ranges, e.g. "3, 7–9". Empty when there are none.</summary>
    public string ColourPageRanges { get; init; } = "";

    public int PagesPrinted => BlackWhitePages + ColourPages;

    public decimal BlackWhiteAmount => BlackWhitePages * BlackWhitePagePrice;
    public decimal ColourAmount => ColourPages * ColourPagePrice;
    public decimal Total => BlackWhiteAmount + ColourAmount;

    public static string NumberFor(PrintJob job) =>
        $"PP-{(job.FinishedUtc ?? job.CreatedUtc).ToLocalTime():yyyyMMdd}-{job.Id[..Math.Min(6, job.Id.Length)].ToUpperInvariant()}";

    public static Invoice For(PrintJob job, DateTimeOffset issuedUtc)
    {
        var printed = job.Batches
            .Where(b => b.State is BatchState.Completed or BatchState.Skipped)
            .SelectMany(b => Enumerable.Range(b.FirstPage, b.PageCount))
            .Distinct()
            .Order()
            .ToList();

        var colour = job.ColourMode == ColourMode.BlackAndWhite || !PageColourMap.IsValid(job.PageColourMap, job.TotalPages)
            ? new List<int>()
            : printed.Where(p => PageColourMap.IsColour(job.PageColourMap!, p)).ToList();

        return new Invoice
        {
            Number = NumberFor(job),
            IssuedUtc = issuedUtc,
            FileName = job.FileName,
            PrinterName = job.PrinterName,
            StartedUtc = job.StartedUtc,
            FinishedUtc = job.FinishedUtc,
            DocumentPages = job.TotalPages,
            BlackWhitePages = printed.Count - colour.Count,
            ColourPages = colour.Count,
            BlackWhitePagePrice = job.BlackWhitePagePrice,
            ColourPagePrice = job.ColourPagePrice,
            PrintedInBlackAndWhite = job.ColourMode == ColourMode.BlackAndWhite,
            ColourPageRanges = PageColourMap.DescribeRanges(colour),
        };
    }
}
