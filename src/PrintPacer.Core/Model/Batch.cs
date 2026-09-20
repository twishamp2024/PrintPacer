namespace PrintPacer.Core.Model;

/// <summary>One contiguous page range that will become exactly one Windows print job.</summary>
public sealed class Batch
{
    public long RowId { get; set; }
    public required string JobId { get; init; }

    /// <summary>1-based batch index within the job.</summary>
    public required int Number { get; init; }

    /// <summary>1-based inclusive page range.</summary>
    public required int FirstPage { get; init; }
    public required int LastPage { get; init; }

    public int PageCount => LastPage - FirstPage + 1;

    public BatchState State { get; set; } = BatchState.Pending;

    public DateTimeOffset? SubmittedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }

    /// <summary>Best available count of pages we believe physically emerged. May be less than PageCount.</summary>
    public int PagesConfirmedPrinted { get; set; }

    public string? Note { get; set; }

    public List<BatchAttempt> Attempts { get; init; } = new();

    public BatchAttempt? LatestAttempt => Attempts.OrderByDescending(a => a.AttemptNumber).FirstOrDefault();

    public string RangeLabel => FirstPage == LastPage
        ? $"Page {FirstPage}"
        : $"Pages {FirstPage}–{LastPage}";
}
