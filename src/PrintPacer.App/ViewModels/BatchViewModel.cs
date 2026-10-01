using System.Windows.Media;
using PrintPacer.Core.Engine;
using PrintPacer.Core.Model;

namespace PrintPacer.App.ViewModels;

/// <summary>
/// One row in the batch history. The row must make the state of that page range obvious at
/// a glance, including the awkward cases: a batch that may have half printed, or one whose
/// outcome nobody can establish.
///
/// Built from an immutable <see cref="BatchSnapshot"/>, so it can never read a batch that
/// the engine is midway through changing.
/// </summary>
public sealed class BatchViewModel : ObservableObject
{
    private readonly BatchSnapshot _batch;
    private readonly bool _isCurrent;

    public BatchViewModel(BatchSnapshot batch, bool isCurrent, string? liveDetail)
    {
        _batch = batch;
        _isCurrent = isCurrent;
        LiveDetail = liveDetail;
    }

    /// <summary>The status colours are looked up when read; after a theme change, read them again.</summary>
    public void RefreshBrushes()
    {
        Raise(nameof(StatusForeground));
        Raise(nameof(StatusBackground));
    }

    public int Number => _batch.Number;
    public string Heading => $"Batch {_batch.Number}";
    public string Range => _batch.RangeLabel;
    public int PageCount => _batch.PageCount;
    public BatchState State => _batch.State;

    public string? LiveDetail { get; }

    public bool NeedsDecision => _batch.State is BatchState.NeedsAttention or BatchState.Failed;

    public string Icon => _batch.State switch
    {
        BatchState.Completed => "✓",
        BatchState.Skipped => "✓",
        BatchState.Failed => "✕",
        BatchState.NeedsAttention => "!",
        BatchState.InFlight => "▶",
        BatchState.Cancelled => "–",
        _ => "○",
    };

    public string StatusText => _batch.State switch
    {
        BatchState.Completed => "Printed",
        BatchState.Skipped => "Accepted",
        BatchState.Failed => "Failed",
        BatchState.NeedsAttention => "Needs checking",
        BatchState.InFlight => _isCurrent && LiveDetail is not null ? LiveDetail : "Printing",
        BatchState.Cancelled => "Cancelled",
        _ => "Waiting",
    };

    public Brush StatusForeground => Resource(_batch.State switch
    {
        BatchState.Completed or BatchState.Skipped => "Success",
        BatchState.Failed => "Danger",
        BatchState.NeedsAttention => "Warning",
        BatchState.InFlight => "Accent",
        _ => "TextMuted",
    });

    public Brush StatusBackground => Resource(_batch.State switch
    {
        BatchState.Completed or BatchState.Skipped => "SuccessSoft",
        BatchState.Failed => "DangerSoft",
        BatchState.NeedsAttention => "WarningSoft",
        BatchState.InFlight => "AccentSoft",
        _ => "SurfaceMuted",
    });

    /// <summary>
    /// The explanation of how the outcome was decided. Shown verbatim so the user can see
    /// the reasoning rather than just a verdict.
    /// </summary>
    public string? Detail
    {
        get
        {
            if (_batch.Note is { Length: > 0 }) return _batch.Note;
            return string.IsNullOrWhiteSpace(_batch.VerdictExplanation) ? null : _batch.VerdictExplanation;
        }
    }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    /// <summary>How many pages of this batch we believe came out, when that is less than all of them.</summary>
    public string? PartialHint =>
        _batch.State == BatchState.NeedsAttention && _batch.PagesConfirmedPrinted is > 0
            ? $"{_batch.PagesConfirmedPrinted} of {_batch.PageCount} sheets counted"
            : null;

    public int ConfirmedPages => _batch.PagesConfirmedPrinted;

    public bool CanResumeFrom =>
        _batch.State == BatchState.NeedsAttention && _batch.PagesConfirmedPrinted is > 0
        && _batch.PagesConfirmedPrinted < _batch.PageCount;

    /// <summary>
    /// Read out by screen readers for the row as a whole. Without this, assistive technology
    /// announces the type name, which tells the user nothing.
    /// </summary>
    public override string ToString()
    {
        var text = $"{Heading}, {Range}, {StatusText}";
        return HasDetail ? $"{text}. {Detail}" : text;
    }

    private static Brush Resource(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
