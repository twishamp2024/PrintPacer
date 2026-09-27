using PrintPacer.Core.Printing;

namespace PrintPacer.Core.Model;

/// <summary>
/// One contiguous range of print units that will become exactly one Windows print job.
///
/// A unit is whatever the job's <see cref="PrintPlan"/> counts in: a page for a plain
/// single-sided job - so <see cref="FirstPage"/>..<see cref="LastPage"/> are source page
/// numbers, as they always were - and a physical sheet for double-sided, booklet and
/// brochure jobs. The property names and database columns predate sheets and are kept.
/// </summary>
public sealed class Batch
{
    public long RowId { get; set; }
    public required string JobId { get; init; }

    /// <summary>1-based batch index within the job.</summary>
    public required int Number { get; init; }

    /// <summary>1-based inclusive range of print units (pages, or sheets for sheet-based jobs).</summary>
    public required int FirstPage { get; init; }
    public required int LastPage { get; init; }

    /// <summary>Print units in the batch: pages, or sheets for sheet-based jobs.</summary>
    public int PageCount => LastPage - FirstPage + 1;

    /// <summary>
    /// The job's plan, which says what each unit number means. Not stored: it is rebuilt from the
    /// job's settings, which is why the batch can stay a plain pair of numbers on disk.
    /// </summary>
    public PrintPlan? Plan { get; set; }

    /// <summary>Printed sides: what the spooler counts as pages and the printer's counter should advance by.</summary>
    public int Impressions => Plan?.ImpressionsFor(FirstPage, LastPage) ?? PageCount;

    /// <summary>The source pages this batch puts on paper.</summary>
    public IReadOnlyList<int> SourcePages => Plan?.SourcePagesFor(FirstPage, LastPage)
                                            ?? Enumerable.Range(FirstPage, PageCount).ToList();

    public BatchState State { get; set; } = BatchState.Pending;

    public DateTimeOffset? SubmittedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }

    /// <summary>Best available count of pages we believe physically emerged. May be less than PageCount.</summary>
    public int PagesConfirmedPrinted { get; set; }

    public string? Note { get; set; }

    public List<BatchAttempt> Attempts { get; init; } = new();

    public BatchAttempt? LatestAttempt => Attempts.OrderByDescending(a => a.AttemptNumber).FirstOrDefault();

    public string RangeLabel => Plan?.DescribeRange(FirstPage, LastPage) ?? (FirstPage == LastPage
        ? $"Page {FirstPage}"
        : $"Pages {FirstPage}–{LastPage}");
}
