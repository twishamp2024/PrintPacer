using System.Windows.Media;
using PrintPacer.Core.Engine;
using PrintPacer.Core.Model;

namespace PrintPacer.App.ViewModels;

/// <summary>
/// One job in the print queue list. Built from the engine's immutable snapshot and updated in
/// place when a newer one arrives, so the list keeps its selection and scroll position while
/// jobs print overnight.
/// </summary>
public sealed class QueueItemViewModel : ObservableObject
{
    public QueueItemViewModel(JobSnapshot job) => Snapshot = job;

    public JobSnapshot Snapshot { get; private set; }
    public string Id => Snapshot.Id;

    /// <summary>1-based place in the list as shown.</summary>
    public int Position { get; private set; }

    public string? Problem { get; private set; }
    public bool CanMoveUp { get; private set; }
    public bool CanMoveDown { get; private set; }

    /// <summary>Whether the row names its printer: only when the jobs in the list do not all share one.</summary>
    public bool ShowPrinter { get; private set; }

    public void Update(JobSnapshot job, int position, string? problem, bool canMoveUp, bool canMoveDown,
        bool showPrinter = true)
    {
        Snapshot = job;
        Position = position;
        Problem = problem;
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
        ShowPrinter = showPrinter;
        Raise(string.Empty);   // every property may have changed
    }

    public string Number => Snapshot.Number > 0 ? $"#{Snapshot.Number}" : "";
    public string FileName => Snapshot.FileName;

    /// <summary>"216 pages · Black &amp; white · Double-sided"</summary>
    public string WhatLine
    {
        get
        {
            var settings = Snapshot.PrintSettings;
            var parts = new List<string>
            {
                Snapshot.SelectedPageCount == Snapshot.TotalPages
                    ? $"{Snapshot.TotalPages} pages"
                    : $"{Snapshot.SelectedPageCount} of {Snapshot.TotalPages} pages",
                Snapshot.ColourMode == ColourMode.BlackAndWhite ? "B&W" : "Colour",
            };

            parts.Add(settings.Layout switch
            {
                PrintLayout.Booklet => "Booklet",
                PrintLayout.Brochure => "Brochure",
                _ => settings.IsDoubleSided ? "Double-sided" : "Single-sided",
            });

            if (settings.PageSelection != PageSelection.AllPages)
                parts.Add(settings.PageSelection == PageSelection.OddPages ? "odd pages" : "even pages");
            if (Snapshot.WantsInvoice) parts.Add("invoice");

            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>"30 pages/batch · 20 min rest", and the printer when the jobs do not all share one.</summary>
    public string HowLine
    {
        get
        {
            var line = $"{Snapshot.PagesPerBatch} {Snapshot.UnitName(Snapshot.PagesPerBatch)}/batch  ·  " +
                       $"{Snapshot.DelayMinutes} min rest";
            if (ShowPrinter) line += $"  ·  {Snapshot.PrinterName}";
            if (Snapshot.Configuration is { StartMode: StartMode.AtTime, ScheduledStartUtc: { } at })
                line += $"  ·  not before {at.ToLocalTime():ddd HH:mm}";
            return line;
        }
    }

    public string StatusText => Describe(Snapshot);

    /// <summary>The words for a job's state, used in the list and on the job screen alike.</summary>
    public static string Describe(JobSnapshot job) => job.State switch
    {
        JobState.Draft => "Not started",
        JobState.Queued when job.HasSentAnything => "Waiting to continue",
        JobState.Queued => "Waiting",
        JobState.WaitingForStart => job.NextBatchDueUtc is { } at ? $"Scheduled {at.ToLocalTime():HH:mm}" : "Scheduled",
        JobState.PreparingBatch or JobState.PrintingBatch or JobState.VerifyingBatch => "Printing",
        JobState.CoolingDown => "Resting",
        JobState.BlockedPrinterOffline or JobState.BlockedPrinterError => "Printer unavailable",
        JobState.Paused => "Paused",
        JobState.NeedsAttention when job.Batches.Any(b => b.State == BatchState.Failed) => "Failed",
        JobState.NeedsAttention when job.Batches.Any(b => b.State == BatchState.NeedsAttention) => "Needs checking",
        JobState.NeedsAttention => "Needs your decision",
        JobState.Completed => "Finished",
        JobState.Failed => "Failed",
        JobState.Cancelled => "Cancelled",
        _ => job.State.ToString(),
    };

    public Brush StatusForeground => Resource(Snapshot.State switch
    {
        JobState.Completed => "Success",
        JobState.NeedsAttention or JobState.BlockedPrinterError or JobState.BlockedPrinterOffline => "Warning",
        JobState.Failed => "Danger",
        JobState.CoolingDown or JobState.WaitingForStart => "Waiting",
        JobState.PreparingBatch or JobState.PrintingBatch or JobState.VerifyingBatch => "Accent",
        _ => "TextSecondary",
    });

    public Brush StatusBackground => Resource(Snapshot.State switch
    {
        JobState.Completed => "SuccessSoft",
        JobState.NeedsAttention or JobState.BlockedPrinterError or JobState.BlockedPrinterOffline => "WarningSoft",
        JobState.Failed => "DangerSoft",
        JobState.CoolingDown or JobState.WaitingForStart => "WaitingSoft",
        JobState.PreparingBatch or JobState.PrintingBatch or JobState.VerifyingBatch => "AccentSoft",
        _ => "SurfaceMuted",
    });

    /// <summary>Why a waiting job has not started, or what a running one is doing, when there is something to say.</summary>
    public string? Detail => Problem ?? Snapshot.State switch
    {
        JobState.Queued or JobState.NeedsAttention or JobState.BlockedPrinterError or JobState.BlockedPrinterOffline =>
            Snapshot.AttentionReason ?? Snapshot.LastPrinterStatus,
        _ => null,
    };

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public Brush DetailForeground => Resource(HasProblem || Snapshot.State is JobState.NeedsAttention
        or JobState.BlockedPrinterError or JobState.BlockedPrinterOffline ? "Warning" : "TextSecondary");
    public bool HasProblem => Problem is not null;

    public bool IsStarted => !Snapshot.IsWaiting && Snapshot.State != JobState.Draft;

    public string ProgressText => $"{Snapshot.PagesDone} / {Snapshot.UnitCount} {Snapshot.UnitName(Snapshot.UnitCount)}";
    public double ProgressPercent => Snapshot.UnitCount == 0 ? 0 : 100.0 * Snapshot.PagesDone / Snapshot.UnitCount;
    public bool ShowProgress => IsStarted;

    public bool IsWaiting => Snapshot.IsWaiting;
    public bool CanRemove => Snapshot.IsWaiting || Snapshot.IsTerminal;

    /// <summary>Read out by screen readers for the row as a whole.</summary>
    public override string ToString() =>
        $"Job {Snapshot.Number}, {FileName}, {StatusText}. {WhatLine}. {HowLine}." +
        (ShowPrinter ? "" : $" Printer {Snapshot.PrinterName}.") + (HasDetail ? $" {Detail}" : "");

    /// <summary>The row's tooltip: everything on it, and the printer, which the row may leave out.</summary>
    public string ToolTipText => $"{FileName}\n{WhatLine}\n{HowLine}" + (ShowPrinter ? "" : $"\nPrinter: {Snapshot.PrinterName}");

    private static Brush Resource(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

/// <summary>A file that could not be added, or something worth knowing about one that was.</summary>
public sealed record IntakeNotice(string FileName, string Message, bool IsError)
{
    public string Icon => IsError ? "✕" : "i";
    public override string ToString() => $"{FileName}: {Message}";
}
