using System.Threading.Channels;
using PrintPacer.Core.Billing;
using PrintPacer.Core.Device;
using PrintPacer.Core.Model;
using PrintPacer.Core.Pdf;
using PrintPacer.Core.Persistence;
using PrintPacer.Core.Printing;
using PrintPacer.Core.Spooler;
using Serilog;

namespace PrintPacer.Core.Engine;

/// <summary>
/// Drives jobs from "configured" to "printed", one batch and one Windows print job at a
/// time, with a rest period in between.
///
/// Everything happens on a single loop fed by a command queue. There is no lock-based
/// sharing of job state with the UI: callers post commands, the loop applies them, and the
/// loop raises change events. That removes an entire category of race conditions from an
/// application whose whole value proposition is being trustworthy while nobody is watching.
///
/// Two invariants are load-bearing:
///
///   1. A submission intent is written to disk and flushed BEFORE the print API is called.
///      There is therefore no instant at which a job can exist in the Windows queue that
///      this application has no record of.
///
///   2. A batch is only ever resubmitted automatically when the evidence says with
///      certainty that nothing printed. Every other outcome stops and asks a human.
///
/// Jobs form one print queue. Each job keeps its own configuration, batches and recovery
/// state; the queue only decides whose turn it is. Exactly one job has the printer at a time,
/// and a job may send a batch only while it does - see <see cref="DispatchQueueAsync"/>.
/// </summary>
public sealed class JobEngine : IAsyncDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<JobEngine>();

    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long to let the device's page counter settle before judging a batch.</summary>
    private static readonly TimeSpan CounterSettleWindow = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CounterSettleInterval = TimeSpan.FromSeconds(6);

    private readonly JobStore _store;
    private readonly ISpooler _spooler;
    private readonly IPrintEngine _printEngine;
    private readonly IDeviceProbeFactory _probes;
    private readonly ISystemClock _clock;
    private readonly INotifier _notifier;
    private readonly IPowerManager _power;
    private readonly IInvoicePrinter _invoicePrinter;

    private readonly Channel<Func<CancellationToken, Task>> _commands =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });

    private readonly List<PrintJob> _jobs = new();
    private readonly Dictionary<string, Deadline> _deadlines = new();
    private readonly Dictionary<string, DateTimeOffset> _lastBlockedNotice = new();
    private readonly HashSet<string> _setupWarned = new();

    /// <summary>
    /// Jobs that need a decision and that the queue has moved past (see
    /// <see cref="AppSettings.ContinueQueueWhenJobNeedsDecision"/>). Not persisted: after a restart
    /// the dispatcher checks again, with the Windows queue as it is then.
    /// </summary>
    private readonly HashSet<string> _setAside = new();

    /// <summary>
    /// The rest before the next queued job, per job. Worked out from stored times, but held as a
    /// <see cref="Deadline"/> so that, like every other wait, a clock that moves backwards - or
    /// was set differently before a restart - cannot stretch it beyond the rest itself.
    /// </summary>
    private readonly Dictionary<string, (DateTimeOffset Due, Deadline Deadline)> _queueRests = new();

    /// <summary>How long the printer's page counter is given to settle after an invoice, before another job starts on it.</summary>
    private static readonly TimeSpan InvoiceSettle = TimeSpan.FromSeconds(90);

    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;
    private ActiveRun? _activeRun;

    private AppSettings _settings;
    private readonly TimeSpan _tickInterval;

    public JobEngine(
        JobStore store,
        ISpooler spooler,
        IPrintEngine printEngine,
        IDeviceProbeFactory probes,
        AppSettings settings,
        ISystemClock? clock = null,
        INotifier? notifier = null,
        IPowerManager? power = null,
        TimeSpan? tickInterval = null,
        IInvoicePrinter? invoicePrinter = null)
    {
        _store = store;
        _spooler = spooler;
        _printEngine = printEngine;
        _probes = probes;
        _settings = settings;
        _clock = clock ?? SystemClock.Instance;
        _notifier = notifier ?? NullNotifier.Instance;
        _power = power ?? NullPowerManager.Instance;
        _tickInterval = tickInterval ?? DefaultTickInterval;
        _invoicePrinter = invoicePrinter ?? NullInvoicePrinter.Instance;
    }

    // ---- Public surface ----------------------------------------------------

    /// <summary>
    /// Raised whenever any job's persisted state changes, carrying an immutable snapshot.
    /// Raised on the engine loop; subscribers are responsible for marshalling to their own
    /// thread, and can do so safely because the snapshot never changes underneath them.
    /// </summary>
    public event Action<JobSnapshot>? JobChanged;

    public event Action<JobEvent>? EventAppended;

    /// <summary>Raised once at startup with the outcome of recovering each unfinished job.</summary>
    public event Action<IReadOnlyList<RecoveryReport>>? RecoveryCompleted;

    /// <summary>
    /// Snapshots of every job, taken on the engine loop. Use this rather than reaching into
    /// the engine's own job objects, which are not safe to read from another thread.
    /// </summary>
    public Task<IReadOnlyList<JobSnapshot>> GetJobsAsync()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<JobSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Post(_ =>
        {
            completion.TrySetResult(_jobs.Select(JobSnapshot.From).ToList());
            return Task.CompletedTask;
        });

        return completion.Task;
    }

    /// <summary>
    /// The live job list. Only safe to read from the engine loop itself; everything outside
    /// the engine should use <see cref="GetJobsAsync"/> or the <see cref="JobChanged"/> snapshot.
    /// </summary>
    internal IReadOnlyList<PrintJob> Jobs => _jobs;

    public AppSettings Settings => _settings;

    public async Task StartAsync()
    {
        _jobs.Clear();
        _jobs.AddRange(_store.LoadAllJobs().OrderBy(j => j.QueuePosition));

        var reports = await RecoverAsync().ConfigureAwait(false);

        _loop = Task.Run(() => RunLoopAsync(_shutdown.Token));

        RecoveryCompleted?.Invoke(reports);
    }

    public void UpdateSettings(AppSettings settings) => Post(_ =>
    {
        _settings = settings;
        return Task.CompletedTask;
    });

    public Task<JobSnapshot> CreateJobAsync(PdfDocumentInfo document, string printerName,
        int pagesPerBatch, int delayMinutes, StartMode startMode, DateTimeOffset? startAtUtc,
        int renderDpi, ColourMode colourMode, InvoiceRequest? invoice = null, PrintSettings? printSettings = null)
        => CreateJobAsync(document, new JobConfiguration
        {
            PrinterName = printerName,
            PagesPerBatch = pagesPerBatch,
            DelayMinutes = delayMinutes,
            StartMode = startMode,
            ScheduledStartUtc = startAtUtc,
            RenderDpi = renderDpi,
            ColourMode = colourMode,
            PrintSettings = printSettings ?? PrintSettings.Default,
            Invoice = invoice,
        });

    /// <summary>
    /// Adds a document to the queue as a new job with its own configuration. The job is a draft:
    /// nothing is sent until it is started (<see cref="StartJob"/> or <see cref="StartQueue"/>).
    /// </summary>
    public Task<JobSnapshot> CreateJobAsync(PdfDocumentInfo document, JobConfiguration configuration)
    {
        // What to print and how it is laid out is settled before the job exists, so a
        // configuration that cannot produce a single sheet is refused here, not at 3 a.m.
        Validate(configuration, document.PageCount);

        var identity = new DocumentIdentity(document.Path, document.FileName, document.FileSizeBytes,
            document.Sha256, document.PageCount, document.Title, document.Author, document.Producer,
            document.PdfVersion, document.IsEncrypted);

        var completion = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        Post(_ =>
        {
            try
            {
                var id = Guid.NewGuid().ToString("N");
                var position = _jobs.Count == 0 ? 1 : _jobs.Max(j => j.QueuePosition) + 1;
                var job = PrintJob.Create(id, _store.AllocateJobNumber(), position, identity, configuration, _clock.UtcNow);

                _store.InsertJob(job);
                _jobs.Add(job);

                Record(job, "Information", "Job", $"Job created for {job.FileName}",
                    $"{job.Label}. {job.Plan.DescribeSize()}, {job.BatchCount} batches of up to {job.PagesPerBatch} " +
                    $"{job.Plan.UnitName(job.PagesPerBatch)}, " +
                    $"{job.DelayMinutes} min between batches, {job.ColourMode.Describe().ToLowerInvariant()}, " +
                    $"printer \"{job.PrinterName}\".");

                RecordConfigurationDetail(job);

                Notify(job);
                completion.TrySetResult(JobSnapshot.From(job));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            return Task.CompletedTask;
        });

        return completion.Task;
    }

    /// <summary>Refuses, in the user's words, a configuration that cannot print this document.</summary>
    private static void Validate(JobConfiguration configuration, int pageCount)
    {
        if (configuration.Invoice is { } invoice && (invoice.BlackWhitePagePrice < 0 || invoice.ColourPagePrice < 0))
            throw new ArgumentOutOfRangeException(nameof(configuration), "Page prices cannot be negative.");

        if (configuration.Problems(pageCount) is { Count: > 0 } problems)
            throw new ArgumentException(problems[0], nameof(configuration));

        // Throws, with the reason, when the settings make no sheets from this document.
        PrintPlan.Build(configuration.PrintSettings, pageCount);
    }

    private void RecordConfigurationDetail(PrintJob job)
    {
        if (job.PrintSettings != PrintSettings.Default)
        {
            Record(job, "Information", "Job", $"Print settings: {job.PrintSettings.Summary()}.",
                job.PrintSettings.IsImposed
                    ? $"{job.PrintSettings.DescribeSpacing()}. {string.Join("; ", job.Plan.DescribeSheets(2))}."
                    : null);
        }

        if (job.WantsInvoice)
        {
            var blackAndWhite = job.ColourMode == ColourMode.BlackAndWhite;
            // Only the pages that will print: an odd- or even-page job never charges the others.
            var selected = job.Plan.SelectedPages;
            var colourPages = blackAndWhite ? 0 : selected.Count(p => PageColourMap.IsColour(job.PageColourMap!, p));
            Record(job, "Information", "Invoice", "An invoice will print when the job finishes.",
                $"{selected.Count - colourPages} black & white page(s) at {job.BlackWhitePagePrice}, " +
                $"{colourPages} colour page(s) at {job.ColourPagePrice}" +
                (blackAndWhite ? " (printing in black & white, so none are charged as colour)." : "."));
        }
    }

    /// <summary>
    /// Changes the configuration of a job that has not started. The job gets a new value and
    /// batches planned afresh from it; no other job, and not the defaults, can be affected.
    /// Refused - with the reason - once anything of the job has been sent.
    /// </summary>
    public Task<JobSnapshot> UpdateJobAsync(string jobId, JobConfiguration configuration)
    {
        var completion = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        Post(_ =>
        {
            try
            {
                var index = _jobs.FindIndex(j => j.Id == jobId);
                if (index < 0) throw new InvalidOperationException("That job is no longer in the queue.");

                var job = _jobs[index];
                if (!job.IsWaiting)
                    throw new InvalidOperationException($"{job.Label} has already started, so its settings can no longer be changed.");

                if (configuration == job.Configuration)
                {
                    completion.TrySetResult(JobSnapshot.From(job));
                    return Task.CompletedTask;
                }

                Validate(configuration, job.TotalPages);

                var updated = job.WithConfiguration(configuration, _clock.UtcNow);
                updated.LastPrinterStatus = job.LastPrinterStatus;
                _store.ReplaceConfiguration(updated);
                _jobs[index] = updated;

                Record(updated, "Information", "Job", "Settings changed.",
                    $"{updated.Label}: {DescribeConfiguration(updated)}.");
                RecordConfigurationDetail(updated);

                Notify(updated);
                completion.TrySetResult(JobSnapshot.From(updated));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            return Task.CompletedTask;
        });

        return completion.Task;
    }

    private static string DescribeConfiguration(PrintJob job) =>
        $"{job.Plan.DescribeSize()}, {job.ColourMode.Describe().ToLowerInvariant()}, " +
        $"{job.PrintSettings.Duplex.Describe().ToLowerInvariant()}, " +
        $"{job.BatchCount} batch{(job.BatchCount == 1 ? "" : "es")} of up to {job.PagesPerBatch} {job.Plan.UnitName(job.PagesPerBatch)}, " +
        $"{job.DelayMinutes} min between batches, printer \"{job.PrinterName}\"" +
        (job.StartMode == StartMode.AtTime && job.ScheduledStartUtc is { } at
            ? $", not before {at.ToLocalTime():dd MMM HH:mm}"
            : "") +
        (job.WantsInvoice ? ", with an invoice" : "");

    /// <summary>
    /// Starts a job: puts it in the print queue. It begins at once when no other job has the
    /// printer, and otherwise when its turn comes. A single job therefore starts exactly as it
    /// always did.
    /// </summary>
    public void StartJob(string jobId) => StartQueue(new[] { jobId });

    /// <summary>
    /// Puts draft jobs in the print queue - every draft when <paramref name="jobIds"/> is null -
    /// to be printed one after another in queue order, each with its own settings.
    /// </summary>
    public void StartQueue(IEnumerable<string>? jobIds = null)
    {
        var wanted = jobIds?.ToHashSet();
        Post(async _ =>
        {
            var queued = _jobs
                .Where(j => j.State == JobState.Draft && (wanted is null || wanted.Contains(j.Id)))
                .OrderBy(j => j.QueuePosition)
                .ToList();

            foreach (var job in queued)
            {
                job.LastError = null;
                job.AttentionReason = null;
                Transition(job, JobState.Queued, "Waiting its turn in the print queue.");
                Record(job, "Information", "Queue", $"{job.Label} added to the print queue: {job.FileName}.",
                    DescribeConfiguration(job) + ".");
            }

            // Straight away when the printer is free, so pressing Start is never followed by a pause.
            await DispatchQueueAsync().ConfigureAwait(false);
        });
    }

    /// <summary>Takes a queued job that has not started back out of the queue. It stays in the list as a draft.</summary>
    public void HoldJob(string jobId) => Post(_ =>
    {
        var job = Find(jobId);
        if (job is not { State: JobState.Queued, IsWaiting: true }) return Task.CompletedTask;

        Transition(job, JobState.Draft, "Held back: it will not start until it is added to the queue again.");
        Record(job, "Information", "Queue", $"{job.Label} held back from the print queue.");
        return Task.CompletedTask;
    });

    /// <summary>
    /// Moves a job that has not started earlier (negative) or later (positive) among the other
    /// jobs that have not started. Jobs already printing or finished keep their places. The new
    /// order is stored before it takes effect.
    /// </summary>
    public void MoveJob(string jobId, int offset) => Post(_ =>
    {
        var job = Find(jobId);
        if (job is not { IsWaiting: true } || offset == 0) return Task.CompletedTask;

        var ordered = _jobs.OrderBy(j => j.QueuePosition).ToList();
        var waiting = ordered.Where(j => j.IsWaiting).ToList();
        var from = waiting.IndexOf(job);
        var to = Math.Clamp(from + offset, 0, waiting.Count - 1);
        if (to == from) return Task.CompletedTask;

        // The waiting jobs trade their slots among themselves, so every other job stays put.
        var slots = waiting.Select(j => j.QueuePosition).OrderBy(p => p).ToList();
        waiting.RemoveAt(from);
        waiting.Insert(to, job);

        // Positions must be distinct for the order to mean anything; they are, unless an older
        // database left duplicates, in which case everything is renumbered first.
        if (slots.Distinct().Count() != slots.Count)
        {
            for (var i = 0; i < ordered.Count; i++) ordered[i].QueuePosition = i + 1;
            slots = waiting.Select(j => j.QueuePosition).OrderBy(p => p).ToList();
        }

        for (var i = 0; i < waiting.Count; i++) waiting[i].QueuePosition = slots[i];

        _store.SaveQueueOrder(_jobs);
        SortJobs();

        Record(job, "Information", "Queue",
            $"{job.Label} moved to position {WaitingPosition(job)} of {_jobs.Count(j => j.IsWaiting)} in the queue.");

        foreach (var moved in waiting) Notify(moved);
        return Task.CompletedTask;
    });

    /// <summary>
    /// Removes a job from the list. Allowed for a job that has not started - nothing of it was
    /// ever sent - and for a finished one. A job still under way must be cancelled first, so its
    /// batch and attempt records are never deleted from under the engine.
    /// </summary>
    /// <returns>Null when the job was removed; otherwise why not.</returns>
    public Task<string?> RemoveJobAsync(string jobId)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Post(_ =>
        {
            var job = Find(jobId);
            string? refusal = job switch
            {
                null => null,
                _ when _activeRun?.Job == job => "It has a batch in the printer's queue. Cancel the job first.",
                { IsWaiting: true } => null,
                { IsTerminal: true, InvoiceState: InvoiceState.Sending } => "Its invoice is being printed. Try again in a moment.",
                { IsTerminal: true } => null,
                _ => "It has started. Cancel it first; its record then stays until you remove it.",
            };

            if (job is not null && refusal is null)
            {
                Record(job, "Information", "Queue", $"{job.Label} removed from the list: {job.FileName}.",
                    job.IsWaiting ? "It had not started; nothing of it was sent." : $"It had {job.State.ToString().ToLowerInvariant()}.");
                _store.DeleteJob(job.Id);
                _jobs.Remove(job);
                _deadlines.Remove(job.Id);
                _setAside.Remove(job.Id);
                _queueRests.Remove(job.Id);
                ReleasePowerIfIdle();
            }

            completion.TrySetResult(refusal);
            return Task.CompletedTask;
        });

        return completion.Task;
    }

    public void PauseJob(string jobId) => Post(_ =>
    {
        var job = Find(jobId);
        if (job is null || job.IsTerminal || job.State == JobState.Paused) return Task.CompletedTask;

        // A job that has not started is held back instead (HoldJob): a paused job has the printer,
        // so pausing one that is only waiting would stop the whole queue behind it.
        if (job.State is JobState.Draft or JobState.Queued) return Task.CompletedTask;

        // A batch already in the queue is deliberately left alone: see PauseSemantics below.
        var note = _activeRun?.Job.Id == jobId
            ? "Paused. The batch already in the printer's queue will finish; nothing further will be sent."
            : "Paused. No further batches will be sent until you resume.";

        Transition(job, JobState.Paused, note);
        return Task.CompletedTask;
    });

    public void ResumeJob(string jobId) => Post(async _ =>
    {
        var job = Find(jobId);
        if (job is null || job.IsTerminal) return;
        if (job.State is not (JobState.Paused or JobState.NeedsAttention
            or JobState.BlockedPrinterOffline or JobState.BlockedPrinterError or JobState.Failed)) return;

        job.LastError = null;
        job.AttentionReason = null;
        job.ConsecutiveBlockedChecks = 0;

        Record(job, "Information", "Job", "Resumed by the user.");
        await TryStartNextBatchAsync(job).ConfigureAwait(false);
    });

    public void CancelJob(string jobId) => Post(async _ =>
    {
        var job = Find(jobId);
        if (job is null || job.IsTerminal) return;

        if (_activeRun?.Job.Id == jobId)
        {
            await AbandonActiveRunAsync("the job was cancelled").ConfigureAwait(false);
        }

        foreach (var batch in job.Batches.Where(b => b.State is BatchState.Pending or BatchState.InFlight))
        {
            batch.State = BatchState.Cancelled;
            _store.UpdateBatch(batch);
        }

        job.FinishedUtc = _clock.UtcNow;
        ClearDeadline(job);
        Transition(job, JobState.Cancelled, "Cancelled by the user.");
        ReleasePowerIfIdle();
    });

    /// <summary>Resubmits one batch. Only reachable from the UI, and always with a warning shown.</summary>
    public void RetryBatch(string jobId, int batchNumber) => Post(async _ =>
    {
        await ReleaseBatchForManualDecisionAsync(jobId, batchNumber).ConfigureAwait(false);

        var job = Find(jobId);
        var batch = job?.Batches.FirstOrDefault(b => b.Number == batchNumber);
        if (job is null || batch is null || _activeRun is not null) return;

        batch.State = BatchState.Pending;
        batch.Note = null;
        _store.UpdateBatch(batch);

        job.CurrentBatchNumber = batchNumber;
        job.AttentionReason = null;
        job.LastError = null;

        Record(job, "Warning", "Batch",
            $"Batch {batchNumber} ({batch.RangeLabel}) is being sent again at the user's request.",
            "The user confirmed this batch had not printed.");

        await TryStartBatchAsync(job, batch).ConfigureAwait(false);
    });

    /// <summary>
    /// Stops monitoring a batch the user is about to resolve by hand.
    ///
    /// Without this, a decision the user has just made can be silently overwritten moments
    /// later by the monitor reaching its own verdict about the same batch. The user's
    /// judgement, made by looking at the actual paper, always wins.
    /// </summary>
    private async Task ReleaseBatchForManualDecisionAsync(string jobId, int batchNumber)
    {
        if (_activeRun is not { } run) return;
        if (run.Job.Id != jobId || run.Batch.Number != batchNumber) return;

        await AbandonActiveRunAsync("you resolved this batch yourself").ConfigureAwait(false);
    }

    /// <summary>The user looked at the paper and confirmed these pages came out.</summary>
    public void ConfirmBatchPrinted(string jobId, int batchNumber) => Post(async _ =>
    {
        await ReleaseBatchForManualDecisionAsync(jobId, batchNumber).ConfigureAwait(false);

        var job = Find(jobId);
        var batch = job?.Batches.FirstOrDefault(b => b.Number == batchNumber);
        if (job is null || batch is null) return;

        batch.State = BatchState.Skipped;
        batch.PagesConfirmedPrinted = batch.PageCount;
        batch.FinishedUtc = _clock.UtcNow;
        batch.Note = "Confirmed as printed by the user.";
        _store.UpdateBatch(batch);

        if (batch.LatestAttempt is { } attempt)
        {
            attempt.Evidence |= CompletionEvidence.UserConfirmedPrinted;
            attempt.State = AttemptState.Verified;
            _store.UpdateAttempt(attempt);
        }

        Record(job, "Information", "Batch",
            $"Batch {batchNumber} ({batch.RangeLabel}) marked as already printed by the user.");

        job.AttentionReason = null;
        await ContinueAfterBatchAsync(job).ConfigureAwait(false);
    });

    /// <summary>The user looked at the paper and confirmed these pages did NOT come out.</summary>
    public void ConfirmBatchNotPrinted(string jobId, int batchNumber) => Post(async _ =>
    {
        await ReleaseBatchForManualDecisionAsync(jobId, batchNumber).ConfigureAwait(false);

        var job = Find(jobId);
        var batch = job?.Batches.FirstOrDefault(b => b.Number == batchNumber);
        if (job is null || batch is null) return;

        if (batch.LatestAttempt is { } attempt)
        {
            attempt.Evidence |= CompletionEvidence.UserConfirmedNotPrinted;
            attempt.State = AttemptState.Failed;
            _store.UpdateAttempt(attempt);
        }

        batch.State = BatchState.Pending;
        batch.PagesConfirmedPrinted = 0;
        batch.Note = "The user confirmed these pages did not print.";
        _store.UpdateBatch(batch);

        job.CurrentBatchNumber = batchNumber;
        job.AttentionReason = null;

        Record(job, "Information", "Batch",
            $"Batch {batchNumber} ({batch.RangeLabel}) confirmed as not printed; it will be sent again.");

        await TryStartBatchAsync(job, batch).ConfigureAwait(false);
    });

    /// <summary>
    /// Splits a partially printed batch: the first <paramref name="printedPages"/> pages are
    /// accepted as done and a new pending batch is created for the remainder. This is how a
    /// mid-batch jam is recovered without reprinting the sheets that did come out.
    /// </summary>
    public void ResumeBatchFrom(string jobId, int batchNumber, int printedPages) => Post(async _ =>
    {
        await ReleaseBatchForManualDecisionAsync(jobId, batchNumber).ConfigureAwait(false);

        var job = Find(jobId);
        var batch = job?.Batches.FirstOrDefault(b => b.Number == batchNumber);
        if (job is null || batch is null) return;

        printedPages = Math.Clamp(printedPages, 0, batch.PageCount);
        if (printedPages == 0) { ConfirmBatchNotPrinted(jobId, batchNumber); return; }
        if (printedPages == batch.PageCount) { ConfirmBatchPrinted(jobId, batchNumber); return; }

        var resumeFrom = batch.FirstPage + printedPages;
        var accepted = job.Plan.DescribeRange(batch.FirstPage, resumeFrom - 1);
        var rest = job.Plan.DescribeRange(resumeFrom, batch.LastPage);

        batch.State = BatchState.Skipped;
        batch.PagesConfirmedPrinted = printedPages;
        batch.FinishedUtc = _clock.UtcNow;
        batch.Note = $"{accepted} confirmed printed; the rest moved to a new batch.";
        _store.UpdateBatch(batch);

        // The remainder becomes its own batch so the page bookkeeping stays exact.
        var remainder = new Batch
        {
            JobId = job.Id,
            Number = NextFreeBatchNumber(job),
            FirstPage = resumeFrom,
            LastPage = batch.LastPage,
            State = BatchState.Pending,
            Note = $"Remainder of batch {batch.Number} after it stopped part-way.",
            Plan = job.Plan,
        };

        job.Batches.Add(remainder);
        _store.InsertBatchForExistingJob(remainder);

        Record(job, "Information", "Batch",
            $"Batch {batchNumber} resumed: {accepted} accepted, {rest} queued as batch {remainder.Number}.");

        job.AttentionReason = null;
        await ContinueAfterBatchAsync(job).ConfigureAwait(false);
    });

    public void DeleteJob(string jobId) => Post(async _ =>
    {
        var job = Find(jobId);
        if (job is null) return;
        if (_activeRun?.Job.Id == jobId) await AbandonActiveRunAsync("the job was deleted").ConfigureAwait(false);

        _store.DeleteJob(jobId);
        _jobs.Remove(job);
        _deadlines.Remove(jobId);
        _setAside.Remove(jobId);
        ReleasePowerIfIdle();
    });

    /// <summary>Cancels the Windows job for the batch currently in flight, without cancelling the whole job.</summary>
    public void CancelCurrentSpoolerJob(string jobId) => Post(async _ =>
    {
        if (_activeRun is not { } run || run.Job.Id != jobId) return;
        await CancelSpoolerJobAsync(run, "the user cancelled the queued batch").ConfigureAwait(false);
    });

    public TimeSpan? RemainingUntilNextBatch(string jobId) =>
        _deadlines.TryGetValue(jobId, out var deadline) ? deadline.Remaining : null;

    /// <summary>
    /// Builds the exportable account of a job: its configuration, every batch, every
    /// submission attempt with the evidence behind its verdict, and the event log.
    ///
    /// Produced on the engine loop so that it can read the full attempt history, which is
    /// deliberately not exposed outside the engine.
    /// </summary>
    public Task<IReadOnlyList<string>> BuildReportAsync(string? jobId)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Post(_ =>
        {
            var lines = new List<string>
            {
                $"{AppPaths.ProductName} report  -  {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
                new('=', 78),
                "",
            };

            foreach (var job in _jobs.Where(j => jobId is null || j.Id == jobId))
            {
                if (job.TryGetPlan() is null)
                {
                    lines.Add($"Document      : {job.FileName}");
                    lines.Add($"Print settings: unreadable ({job.PrintSettings.Summary()}); this job cannot print.");
                    lines.Add("");
                    continue;
                }

                lines.Add($"Job           : {job.Label}, queue position {job.QueuePosition}");
                lines.Add($"Document      : {job.FileName}");
                lines.Add($"Source        : {job.SourcePath}");
                lines.Add($"SHA-256       : {job.FileSha256}");
                lines.Add($"Pages         : {job.TotalPages}");
                lines.Add($"Printing      : {job.Plan.DescribeSize()}");
                lines.Add($"Page selection: {job.PrintSettings.PageSelection.Describe()}");
                lines.Add($"Layout        : {job.PrintSettings.Layout.Describe()}" +
                          (job.PrintSettings.Layout switch
                          {
                              PrintLayout.Booklet => $", {job.PrintSettings.Binding.Describe().ToLowerInvariant()}" +
                                                     (job.PrintSettings.SheetsPerSignature > 0
                                                         ? $", sections of {job.PrintSettings.SheetsPerSignature} sheets"
                                                         : ", one section") +
                                                     (job.PrintSettings.KeepLastPageAsBackCover ? ", last page kept as back cover" : ""),
                              PrintLayout.Brochure => $", {job.PrintSettings.Fold.Describe().ToLowerInvariant()}",
                              _ => "",
                          }));
                lines.Add($"Sides         : {job.PrintSettings.Duplex.Describe()}");
                if (job.PrintSettings.IsImposed)
                {
                    lines.Add($"Paper         : {job.PrintSettings.Paper.Describe()}, landscape");
                    lines.Add($"Spacing       : {job.PrintSettings.DescribeSpacing()}");
                }
                lines.Add($"Printer       : {job.PrinterName}");
                lines.Add($"Batch size    : {job.PagesPerBatch} {job.Plan.UnitName(job.PagesPerBatch)}");
                lines.Add($"Rest between  : {job.DelayMinutes} minutes");
                lines.Add($"Print mode    : {job.ColourMode.Describe()}");
                lines.Add($"State         : {job.State}");
                lines.Add($"Created       : {job.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                if (job.StartedUtc is { } started)
                    lines.Add($"Started       : {started.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                if (job.FinishedUtc is { } finished)
                    lines.Add($"Finished      : {finished.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                if (job.AttentionReason is { Length: > 0 } attention)
                    lines.Add($"Needs action  : {attention}");

                if (job.WantsInvoice)
                {
                    var invoice = Invoice.For(job, _clock.UtcNow);
                    lines.Add($"Invoice       : {invoice.Number}, {job.InvoiceState}" +
                              (job.InvoicePrintedUtc is { } printed ? $" at {printed.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : ""));
                    lines.Add($"  B&W pages   : {invoice.BlackWhitePages} x {invoice.BlackWhitePagePrice} = {invoice.BlackWhiteAmount}");
                    lines.Add($"  Colour pages: {invoice.ColourPages} x {invoice.ColourPagePrice} = {invoice.ColourAmount}" +
                              (invoice.ColourPageRanges.Length > 0 ? $"  ({invoice.ColourPageRanges})" : ""));
                    lines.Add($"  Total       : {invoice.Total}");
                }

                lines.Add("");
                lines.Add("Batches");
                lines.Add(new string('-', 78));

                foreach (var batch in job.Batches.OrderBy(b => b.FirstPage))
                {
                    lines.Add($"  Batch {batch.Number,-3} {batch.RangeLabel,-18} {batch.State,-15} " +
                              $"confirmed {batch.PagesConfirmedPrinted}/{batch.PageCount} {job.Plan.UnitName(batch.PageCount)}" +
                              (job.Plan.Unit == PrintUnit.Sheet ? $", {batch.Impressions} printed sides" : ""));

                    foreach (var attempt in batch.Attempts.OrderBy(a => a.AttemptNumber))
                    {
                        lines.Add($"      attempt {attempt.AttemptNumber}: {attempt.State}");
                        lines.Add($"        spooler document : {attempt.DocumentToken}");
                        lines.Add($"        spooler job id   : {attempt.SpoolerJobId?.ToString() ?? "never seen"}");
                        lines.Add($"        pages reported   : {attempt.SpoolerPagesPrinted}");
                        lines.Add($"        printer counter  : " +
                                  $"{attempt.DeviceImpressionsBefore?.ToString() ?? "-"} -> " +
                                  $"{attempt.DeviceImpressionsAfter?.ToString() ?? "-"} " +
                                  $"(delta {attempt.DeviceImpressionDelta?.ToString() ?? "-"})");
                        if (attempt.DeviceJamDelta is > 0 || attempt.DeviceMispickDelta is > 0)
                        {
                            lines.Add($"        printer faults   : {attempt.DeviceJamDelta ?? 0} jam(s), " +
                                      $"{attempt.DeviceMispickDelta ?? 0} feed failure(s)");
                        }
                        lines.Add($"        evidence         : {attempt.Evidence}");
                        if (attempt.VerdictExplanation is { Length: > 0 } verdict)
                            lines.Add($"        verdict          : {verdict}");
                        if (attempt.Error is { Length: > 0 } error)
                            lines.Add($"        error            : {error}");
                    }
                }

                lines.Add("");
            }

            lines.Add("Events");
            lines.Add(new string('-', 78));

            foreach (var entry in _store.LoadEvents(jobId, 5000))
            {
                lines.Add($"{entry.Utc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  [{entry.Level,-11}] " +
                          $"{entry.Category,-9} {entry.Message}");
                if (entry.Detail is { Length: > 0 } detail)
                    lines.Add($"{new string(' ', 34)}{detail}");
            }

            completion.TrySetResult(lines);
            return Task.CompletedTask;
        });

        return completion.Task;
    }

    // ---- Main loop ---------------------------------------------------------

    private async Task RunLoopAsync(CancellationToken ct)
    {
        Log.Information("Engine loop started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (_commands.Reader.TryRead(out var command))
                {
                    try
                    {
                        await command(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Engine command failed");
                    }
                }

                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must never die: a crashed scheduler mid-job is exactly the
                // failure the user would discover in the morning.
                Log.Error(ex, "Unhandled error in the engine loop");
            }

            try { await Task.Delay(_tickInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        Log.Information("Engine loop stopped");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_activeRun is not null)
        {
            await MonitorActiveRunAsync(_activeRun, ct).ConfigureAwait(false);
            return;
        }

        foreach (var job in _jobs.ToList())
        {
            if (_activeRun is not null) break;
            await TickIdleJobAsync(job, ct).ConfigureAwait(false);
        }

        if (_activeRun is null) await DispatchQueueAsync().ConfigureAwait(false);
        if (_activeRun is null) SendDeferredInvoices();

        ReleasePowerIfIdle();
    }

    // ---- The print queue ----------------------------------------------------------

    /// <summary>
    /// Starts the next queued job when its turn has come.
    ///
    /// Its turn comes when no other job has the printer (<see cref="HoldsPrinter"/>) and the
    /// printer has had its rest after the previous job on it. Queued jobs are taken strictly in
    /// queue order - never by size, name or printer - and each runs with its own settings.
    /// </summary>
    private async Task DispatchQueueAsync()
    {
        if (_activeRun is not null) return;

        var next = _jobs.Where(j => j.State == JobState.Queued).OrderBy(j => j.QueuePosition).FirstOrDefault();
        if (next is null) return;

        if (PrinterHolder() is { } holder)
        {
            if (!TrySetAside(holder, next))
            {
                SetQueueNote(next, holder.State == JobState.NeedsAttention
                    ? $"Next in the queue. {holder.Label} ({holder.FileName}) needs your decision first."
                    : $"Next in the queue, after {holder.Label} ({holder.FileName}).");
                return;
            }

            if (PrinterHolder() is { } stillHeld)
            {
                SetQueueNote(next, $"Next in the queue, after {stillHeld.Label} ({stillHeld.FileName}).");
                return;
            }
        }

        // The printer rests between jobs exactly as it does between batches: printing the next
        // document straight after the last batch of the previous one would defeat the pacing.
        if (RestBeforeStarting(next) is { } rest && QueueRest(next, rest.Due, rest.Length) is { IsDue: false } deadline)
        {
            var starts = _clock.UtcNow + deadline.Remaining;
            SetQueueNote(next, $"Next in the queue. Starts at {starts.ToLocalTime():HH:mm}, {rest.Reason}.");
            if (_settings.KeepAwakeWhilePrinting && !_power.IsHoldingRequest)
                _power.Acquire($"{AppPaths.ProductName} is waiting to print {next.FileName}");
            return;
        }

        await BeginQueuedJobAsync(next).ConfigureAwait(false);
    }

    /// <summary>Gives a queued job the printer: its start time, if it has one, or its first batch.</summary>
    private async Task BeginQueuedJobAsync(PrintJob job)
    {
        var resuming = job.HasSentAnything;
        job.StartedUtc ??= _clock.UtcNow;
        job.LastError = null;
        job.AttentionReason = null;

        var waiting = _jobs.Count(j => j != job && j.State == JobState.Queued);
        Record(job, "Information", "Queue",
            resuming ? $"{job.Label} continues: {job.FileName}." : $"{job.Label} started: {job.FileName}.",
            waiting == 0 ? "No other jobs are waiting." : $"{waiting} more job(s) waiting in the queue.");

        if (job.StartMode == StartMode.AtTime && job.ScheduledStartUtc is { } scheduled && scheduled > _clock.UtcNow)
        {
            SetDeadline(job, scheduled, scheduled - _clock.UtcNow);
            Transition(job, JobState.WaitingForStart,
                $"Waiting until {scheduled.ToLocalTime():dd MMM HH:mm} to start.");
            return;
        }

        await TryStartNextBatchAsync(job).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a job has the printer: it has started, is not finished, and the queue has not
    /// moved past it. Only such a job may send a batch, and only one should exist.
    /// </summary>
    private bool HoldsPrinter(PrintJob job) =>
        _activeRun?.Job == job ||
        job.State switch
        {
            JobState.WaitingForStart or JobState.PreparingBatch or JobState.PrintingBatch or JobState.VerifyingBatch
                or JobState.CoolingDown or JobState.BlockedPrinterOffline or JobState.BlockedPrinterError
                or JobState.Paused => true,
            JobState.NeedsAttention => !_setAside.Contains(job.Id),
            _ => false,
        };

    /// <summary>The job that has the printer, other than <paramref name="except"/>, if any.</summary>
    private PrintJob? PrinterHolder(PrintJob? except = null) =>
        _jobs.Where(j => j != except && HoldsPrinter(j)).OrderBy(j => j.QueuePosition).FirstOrDefault();

    /// <summary>
    /// Moves the queue past a job that needs a decision, when that is allowed and safe.
    ///
    /// Safe means none of the job's pages can still be on their way to the printer: no batch in
    /// flight, and no submission of a batch in doubt still in the Windows queue. Otherwise the
    /// next job's pages could interleave with it, and its page counter would count both. The job
    /// itself is untouched: it keeps its place, settings and unprinted batches, and waits for the
    /// user, who can resolve it at any time.
    /// </summary>
    private bool TrySetAside(PrintJob holder, PrintJob next)
    {
        if (holder.State != JobState.NeedsAttention || !_settings.ContinueQueueWhenJobNeedsDecision) return false;
        if (_activeRun?.Job == holder) return false;
        if (holder.Batches.Any(b => b.State == BatchState.InFlight)) return false;

        foreach (var batch in holder.Batches.Where(b => b.State is BatchState.NeedsAttention or BatchState.Failed))
        {
            if (batch.LatestAttempt is not { } attempt) continue;

            var queued = _spooler.ListJobs(attempt.PrinterName, out var querySucceeded);
            if (!querySucceeded) return false;   // could not ask is not "gone"
            if (queued.Any(j => string.Equals(j.Document, attempt.DocumentToken, StringComparison.Ordinal) && !j.IsCompleted))
                return false;
        }

        _setAside.Add(holder.Id);
        Record(holder, "Warning", "Queue",
            $"{holder.Label} needs your decision; the queue carries on with {next.Label}.",
            $"{holder.FileName} keeps its place, its settings and every batch not yet printed. Nothing of it " +
            "will be sent again until you decide. " + (holder.AttentionReason ?? ""));
        NotifyUser(NotificationKind.Warning, $"{holder.Label} needs your decision",
            $"{holder.FileName} is waiting for you. The queue has moved on to {next.FileName}.", holder);
        return true;
    }

    /// <summary>
    /// When the printer may start <paramref name="next"/>: after the rest the previous job on the
    /// same printer asks for after its last batch, and after any invoice has left the printer.
    /// Worked out from what is stored, so it holds across a restart.
    /// </summary>
    private (DateTimeOffset Due, TimeSpan Length, string Reason)? RestBeforeStarting(PrintJob next)
    {
        (DateTimeOffset Due, TimeSpan Length, string Reason)? latest = null;

        foreach (var job in _jobs.Where(j => j != next &&
                     string.Equals(j.PrinterName, next.PrinterName, StringComparison.OrdinalIgnoreCase)))
        {
            if (job.InvoiceState == InvoiceState.Sending)
                return (_clock.UtcNow + InvoiceSettle, InvoiceSettle, $"after the invoice for {job.Label} has printed");

            var lastPrinted = job.Batches
                .Select(b => b.State == BatchState.Completed ? b.FinishedUtc
                    : b.State is BatchState.NeedsAttention or BatchState.Failed ? b.LatestAttempt?.FinishedUtc
                    : null)
                .Where(t => t is not null)
                .Max();

            if (lastPrinted is { } printed && job.DelayMinutes > 0)
                Consider(printed, TimeSpan.FromMinutes(job.DelayMinutes),
                    $"after the printer rests {job.DelayMinutes} min from {job.Label}");

            if (job.InvoicePrintedUtc is { } invoice)
                Consider(invoice, InvoiceSettle, $"after the invoice for {job.Label}");
        }

        return latest;

        void Consider(DateTimeOffset from, TimeSpan length, string reason)
        {
            var due = from + length;
            if (latest is null || due > latest.Value.Due) latest = (due, length, reason);
        }
    }

    /// <summary>
    /// The wait before <paramref name="job"/> may start, as a <see cref="Deadline"/>: due when the
    /// wall clock reaches <paramref name="due"/>, or when the rest's full length has passed on the
    /// monotonic clock - whichever is first. Kept while the stored times it came from stay the same.
    /// </summary>
    private Deadline QueueRest(PrintJob job, DateTimeOffset due, TimeSpan length)
    {
        if (_queueRests.TryGetValue(job.Id, out var held) && held.Due == due) return held.Deadline;

        var planned = due - _clock.UtcNow;
        var deadline = new Deadline(_clock, due, planned > length ? length : planned);
        _queueRests[job.Id] = (due, deadline);
        return deadline;
    }

    /// <summary>
    /// A job whose batch was resolved by hand while another job had the printer waits for its
    /// turn in the queue, at its old place, rather than printing in the middle of that job.
    /// </summary>
    private void ParkInQueue(PrintJob job, PrintJob holder)
    {
        ClearDeadline(job);
        Transition(job, JobState.Queued, $"Waiting its turn: {holder.Label} ({holder.FileName}) has the printer.");
        Record(job, "Information", "Queue",
            $"{job.Label} will continue when its turn comes; {holder.Label} has the printer.");
    }

    /// <summary>Shows why the next queued job has not started, writing only when that changes.</summary>
    private void SetQueueNote(PrintJob job, string note)
    {
        if (job.LastPrinterStatus == note) return;
        job.LastPrinterStatus = note;
        _store.UpdateJob(job);
        Notify(job);
    }

    private int WaitingPosition(PrintJob job) =>
        _jobs.Where(j => j.IsWaiting).OrderBy(j => j.QueuePosition).ToList().IndexOf(job) + 1;

    private void SortJobs()
    {
        var ordered = _jobs.OrderBy(j => j.QueuePosition).ToList();
        _jobs.Clear();
        _jobs.AddRange(ordered);
    }

    /// <summary>
    /// Sends invoices held back because another job's batch was on the same printer when their
    /// job finished: one extra page mid-batch would throw that batch's page count out.
    /// </summary>
    private void SendDeferredInvoices()
    {
        foreach (var job in _jobs.Where(j => j is { State: JobState.Completed, InvoiceState: InvoiceState.Waiting }).ToList())
            BeginInvoicePrint(job);
    }

    private async Task TickIdleJobAsync(PrintJob job, CancellationToken ct)
    {
        switch (job.State)
        {
            case JobState.WaitingForStart:
            case JobState.CoolingDown:
                if (_deadlines.TryGetValue(job.Id, out var deadline) && deadline.IsDue)
                    await TryStartNextBatchAsync(job).ConfigureAwait(false);
                break;

            case JobState.BlockedPrinterOffline:
            case JobState.BlockedPrinterError:
                if (_deadlines.TryGetValue(job.Id, out var retry) && retry.IsDue)
                    await RetryAfterBlockAsync(job, ct).ConfigureAwait(false);
                break;
        }
    }

    // ---- Starting a batch ---------------------------------------------------

    private async Task TryStartNextBatchAsync(PrintJob job)
    {
        // An unresolved batch blocks everything after it. Printing batch 3 while batch 2 is
        // still in doubt would put the pages in the output tray out of order, and would bury
        // the question the user still has to answer.
        var unresolved = job.Batches
            .Where(b => b.State is BatchState.NeedsAttention or BatchState.Failed)
            .OrderBy(b => b.FirstPage)
            .FirstOrDefault();

        if (unresolved is not null)
        {
            var reason = unresolved.Note ??
                         $"Batch {unresolved.Number} ({unresolved.RangeLabel}) needs checking before " +
                         "the rest of the document can be printed.";

            job.AttentionReason = reason;
            job.CurrentBatchNumber = unresolved.Number;
            ClearDeadline(job);
            Transition(job, JobState.NeedsAttention, reason);
            return;
        }

        var batch = job.Batches
            .Where(b => b.State == BatchState.Pending)
            .OrderBy(b => b.FirstPage)
            .FirstOrDefault();

        if (batch is null)
        {
            CompleteJob(job);
            return;
        }

        await TryStartBatchAsync(job, batch).ConfigureAwait(false);
    }

    private async Task TryStartBatchAsync(PrintJob job, Batch batch)
    {
        if (_activeRun is not null) return;

        // Only the job that has the printer may send. Any other waits for its turn.
        if (PrinterHolder(except: job) is { } holder)
        {
            ParkInQueue(job, holder);
            return;
        }

        // A job taking the printer over - from the queue, or resumed by hand after the queue moved
        // past it - first lets the printer rest from the job before it, as the queue would.
        if (!HoldsPrinter(job) && RestBeforeStarting(job) is { } rest &&
            QueueRest(job, rest.Due, rest.Length) is { IsDue: false } resting)
        {
            var starts = _clock.UtcNow + resting.Remaining;
            ClearDeadline(job);
            Transition(job, JobState.Queued, $"Next in the queue. Starts at {starts.ToLocalTime():HH:mm}, {rest.Reason}.");
            Record(job, "Information", "Queue", $"{job.Label} will continue at {starts.ToLocalTime():HH:mm}, {rest.Reason}.");
            return;
        }

        _queueRests.Remove(job.Id);

        ClearDeadline(job);
        job.CurrentBatchNumber = batch.Number;

        // ---- The document must still be the document we planned against ----
        var verification = PdfService.VerifySource(job.SourcePath, job.FileSha256, job.FileSizeBytes);
        if (verification != SourceVerification.Unchanged)
        {
            var reason = verification switch
            {
                SourceVerification.Missing =>
                    $"The PDF is no longer at {job.SourcePath}. Put the file back, or cancel this job.",
                SourceVerification.Changed =>
                    "The PDF file has changed since this job was set up. Page numbers may no longer line up, " +
                    "so nothing further will be printed automatically.",
                _ => "The PDF file could not be read. Check that it is not open in another program.",
            };

            job.AttentionReason = reason;
            Transition(job, JobState.NeedsAttention, reason);
            NotifyUser(NotificationKind.Problem, "Document problem", reason, job);
            return;
        }

        // ---- The print settings must still make a plan -----------------------
        // They were valid when the job was created, so this only fails for a job whose stored
        // settings were damaged. Printing it with different settings would be worse than stopping.
        IReadOnlyList<PrintSide> sides;
        try
        {
            sides = job.Plan.SidesFor(batch.FirstPage, batch.LastPage);
        }
        catch (Exception ex)
        {
            var reason = $"This job's print settings could not be applied ({ex.Message}). Nothing was sent.";
            job.AttentionReason = reason;
            Transition(job, JobState.NeedsAttention, reason);
            return;
        }

        // ---- The printer must be able to take work --------------------------
        var readiness = await CheckPrinterAsync(job, _shutdown.Token).ConfigureAwait(false);
        job.LastPrinterStatus = readiness.StatusText;

        if (!readiness.Ready)
        {
            EnterBlocked(job, readiness);
            return;
        }

        job.ConsecutiveBlockedChecks = 0;

        // ---- Capture the physical counter, then record the intent -----------
        var counters = await ReadCountersAsync(job, _shutdown.Token).ConfigureAwait(false);

        // This call does not return until the intent is durably on disk. Everything after
        // this point is recoverable; everything before it could not have created a job.
        var attempt = _store.RecordSubmitIntent(job, batch, counters);

        batch.State = BatchState.InFlight;
        batch.SubmittedUtc = _clock.UtcNow;
        _store.UpdateBatch(batch);

        if (_settings.KeepAwakeWhilePrinting && !_power.IsHoldingRequest)
            _power.Acquire($"{AppPaths.ProductName} is printing {job.FileName}");

        var request = new PrintRequest
        {
            SourcePath = job.SourcePath,
            FirstPage = batch.FirstPage,
            LastPage = batch.LastPage,
            PrinterName = job.PrinterName,
            DocumentToken = attempt.DocumentToken,
            RenderDpi = job.RenderDpi,
            ColourMode = job.ColourMode,
            FitToPrintableArea = job.FitToPrintableArea,
            AutoRotate = job.AutoRotate,
            Sides = sides,
            Setup = job.PrintSettings.PrinterSetup,
            Imposition = job.PrintSettings.IsImposed ? ImpositionOptions.From(job.PrintSettings) : null,
        };

        var cancellation = new CancellationTokenSource();
        var submission = _printEngine.SubmitAsync(request, cancellation.Token);

        _activeRun = new ActiveRun
        {
            Job = job,
            Batch = batch,
            Attempt = attempt,
            Submission = submission,
            Cancellation = cancellation,
            StartedUtc = _clock.UtcNow,
            LastProgressUtc = _clock.UtcNow,
        };

        Record(job, "Information", "Batch",
            $"Batch {batch.Number} of {job.BatchCount} submitted ({batch.RangeLabel}).",
            $"Attempt {attempt.AttemptNumber}, spooler document \"{attempt.DocumentToken}\", " +
            $"printer counter before: {counters?.Impressions?.ToString() ?? "unavailable"}." +
            (job.PrintSettings == PrintSettings.Default
                ? ""
                : $" {request.PageCount} printed side(s); {job.PrintSettings.Summary()}."));

        Transition(job, JobState.PrintingBatch,
            $"Printing batch {batch.Number} of {job.BatchCount} ({batch.RangeLabel}).");
    }

    // ---- Monitoring an in-flight batch --------------------------------------

    private async Task MonitorActiveRunAsync(ActiveRun run, CancellationToken ct)
    {
        var job = run.Job;
        var attempt = run.Attempt;

        // Poll the queue at the configured interval rather than on every tick. The tick
        // itself stays at one second so countdowns and cancellation remain responsive.
        // Zero means poll on every tick, which the test suite uses so that a simulated
        // printer cannot race ahead of the monitor.
        if (_settings.SpoolerPollSeconds > 0)
        {
            if (run.NextSpoolerPollUtc is { } due && _clock.UtcNow < due) return;
            run.NextSpoolerPollUtc = _clock.UtcNow +
                TimeSpan.FromSeconds(Math.Min(_settings.SpoolerPollSeconds, 60));
        }

        // ---- Observe the Windows queue --------------------------------------
        var jobs = _spooler.ListJobs(job.PrinterName, out var querySucceeded);
        var mine = querySucceeded
            ? jobs.FirstOrDefault(j => string.Equals(j.Document, attempt.DocumentToken, StringComparison.Ordinal))
            : null;

        if (!querySucceeded)
        {
            // We could not ask. That is emphatically not the same as "the job is gone",
            // so we simply wait and try again on the next tick.
            run.ConsecutiveQueryFailures++;
            if (run.ConsecutiveQueryFailures == 15)
            {
                Record(job, "Warning", "Printer",
                    "The Windows print queue cannot be read right now; still waiting for the batch.",
                    $"Printer \"{job.PrinterName}\".");
            }
            return;
        }

        run.ConsecutiveQueryFailures = 0;

        if (mine is not null)
        {
            run.SawJobInQueue = true;

            if (!attempt.ObservedInQueue)
            {
                attempt.ObservedInQueue = true;
                attempt.SpoolerJobId = mine.JobId;
                attempt.SubmittedUtc ??= _clock.UtcNow;
                attempt.State = AttemptState.Submitted;
                _store.UpdateAttempt(attempt);
                RecordColourSeenInQueue(job, run.Batch, mine);
                RecordSidesSeenInQueue(job, run.Batch, mine);
            }

            if (mine.IsPrinting && !attempt.ObservedPrinting)
            {
                attempt.ObservedPrinting = true;
                attempt.State = AttemptState.Printing;
                _store.UpdateAttempt(attempt);
            }

            if (mine.PagesPrinted > attempt.SpoolerPagesPrinted)
            {
                attempt.SpoolerPagesPrinted = mine.PagesPrinted;
                run.LastProgressUtc = _clock.UtcNow;
                _store.UpdateAttempt(attempt);
                Notify(job);
            }

            if (mine.StatusText != attempt.LastSpoolerStatus)
            {
                attempt.LastSpoolerStatus = mine.StatusText;
                _store.UpdateAttempt(attempt);
                Notify(job);
            }

            run.SpoolerReportedPrintedFlag |= mine.IsCompleted;
            run.SpoolerReportedError |= mine.IsErrored;

            // A retained job has finished printing but is deliberately kept in the queue by
            // the driver. Treat it as finished rather than waiting for it to disappear.
            var finishedButRetained = mine.IsRetained && !mine.IsPrinting && !mine.IsSpooling &&
                                      mine.PagesPrinted >= run.Batch.Impressions;

            if (!finishedButRetained)
            {
                await CheckForStallAsync(run, mine, ct).ConfigureAwait(false);
                return;
            }
        }

        // ---- The job is no longer active in the queue -----------------------
        if (!run.Submission.IsCompleted)
        {
            // Still spooling; the job simply has not appeared yet.
            await CheckForStallAsync(run, null, ct).ConfigureAwait(false);
            return;
        }

        run.Result ??= await run.Submission.ConfigureAwait(false);
        run.LeftQueueUtc ??= _clock.UtcNow;

        // Refused before anything was spooled: there is no counter movement to wait for.
        if (run.Result.SetupRejected)
        {
            await ConcludeAttemptAsync(run, ct).ConfigureAwait(false);
            return;
        }

        // ---- Let the printer's physical counter settle ----------------------
        // The counter is updated by the device asynchronously, so reading it the instant
        // the queue empties would systematically under-count and manufacture false alarms.
        if (!await CountersSettledAsync(run, ct).ConfigureAwait(false)) return;

        await ConcludeAttemptAsync(run, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Windows keeps the settings each job was created with, so for a black and white job the
    /// queue itself can confirm that the request reached the printer - or show that the
    /// driver swapped it for colour. A job that carries no colour setting at all says
    /// neither, and the print engine has already reported that driver when it spooled.
    /// </summary>
    private void RecordColourSeenInQueue(PrintJob job, Batch batch, SpoolerJob queued)
    {
        if (job.ColourMode != ColourMode.BlackAndWhite) return;

        if (queued.Monochrome == true)
        {
            Record(job, "Information", "Printer",
                $"Windows confirms batch {batch.Number} is set to black & white.",
                $"Print job #{queued.JobId} carries the monochrome colour setting.");
        }
        else if (queued.Monochrome == false)
        {
            Record(job, "Warning", "Printer",
                $"Windows shows batch {batch.Number} set to colour, not black & white.",
                $"The driver replaced the black & white setting on print job #{queued.JobId}. " +
                "The pages were still sent in greyscale, but the printer may use colour ink for them.");
        }
    }

    /// <summary>
    /// The same proof for double-sided printing: the DEVMODE Windows stored with the queued job
    /// says whether the driver is really printing both sides. The engine checked before
    /// spooling, so a mismatch here means the driver changed its mind - which the user hears
    /// about, because a booklet printed on one side is not a booklet.
    /// </summary>
    private void RecordSidesSeenInQueue(PrintJob job, Batch batch, SpoolerJob queued)
    {
        var wanted = job.PrintSettings.Duplex;
        if (wanted == DuplexMode.SingleSided) return;

        if (queued.Duplex == wanted)
        {
            Record(job, "Information", "Printer",
                $"Windows confirms batch {batch.Number} is set to {wanted.Describe().ToLowerInvariant()}.",
                $"Print job #{queued.JobId} carries the double-sided setting.");
        }
        else if (queued.Duplex is { } other)
        {
            Record(job, "Warning", "Printer",
                $"Windows shows batch {batch.Number} set to {other.Describe().ToLowerInvariant()}, " +
                $"not {wanted.Describe().ToLowerInvariant()}.",
                $"The driver replaced the double-sided setting on print job #{queued.JobId}. " +
                "Check the sheets of this batch before continuing.");
            NotifyUser(NotificationKind.Warning, $"Batch {batch.Number} may not be double-sided",
                "The printer driver changed the double-sided setting. Check the printed sheets.", job);
        }
    }

    /// <summary>
    /// Detects a batch that has stopped making progress. This is the exact failure the
    /// application exists to manage, so it is never answered by resubmitting.
    /// </summary>
    private async Task CheckForStallAsync(ActiveRun run, SpoolerJob? queued, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMinutes(Math.Max(1, _settings.StallTimeoutMinutes));
        if (_clock.UtcNow - run.LastProgressUtc < timeout) return;
        if (run.StallReported) return;

        run.StallReported = true;

        var counters = await ReadCountersAsync(run.Job, ct).ConfigureAwait(false);
        run.Attempt.DeviceImpressionsAfter = counters?.Impressions;
        run.Attempt.DeviceJamEventsAfter = counters?.JamEvents;
        run.Attempt.DeviceMispickEventsAfter = counters?.MispickEvents;
        _store.UpdateAttempt(run.Attempt);

        var produced = run.Attempt.DeviceImpressionDelta;
        var sidesWord = run.Job.Plan.Unit == PrintUnit.Page ? "sheets" : "printed sides";
        var detail = produced is { } count
            ? $"The printer has produced {count} of {run.Batch.Impressions} {sidesWord} for this batch."
            : $"Windows reports {run.Attempt.SpoolerPagesPrinted} of {run.Batch.Impressions} pages.";

        var reason =
            $"Batch {run.Batch.Number} has not progressed for {_settings.StallTimeoutMinutes} minutes. " +
            $"{detail} The printer may need attention. Nothing will be resent automatically.";

        Record(run.Job, "Warning", "Batch", $"Batch {run.Batch.Number} appears stalled.",
            detail + (queued is null ? "" : $" Queue status: {queued.StatusText}."));

        run.Job.AttentionReason = reason;
        Transition(run.Job, JobState.NeedsAttention, reason);
        NotifyUser(NotificationKind.Problem, "Printing has stalled", reason, run.Job);
    }

    private async Task<bool> CountersSettledAsync(ActiveRun run, CancellationToken ct)
    {
        var probe = await _probes.GetProbeAsync(run.Job.PrinterName, ct).ConfigureAwait(false);
        if (!probe.IsConfigured || run.Attempt.DeviceImpressionsBefore is null)
        {
            // Nothing to settle: no physical counter is available for this printer.
            return true;
        }

        var now = _clock.UtcNow;
        if (run.NextCounterReadUtc is { } next && now < next) return false;

        var snapshot = await probe.ProbeAsync(ct).ConfigureAwait(false);
        var counter = snapshot.VerificationCounter;
        run.NextCounterReadUtc = now + CounterSettleInterval;

        if (counter is null)
        {
            // The device stopped answering. Give up on settling rather than hanging.
            return now - run.LeftQueueUtc!.Value > CounterSettleInterval * 2;
        }

        var expected = run.Attempt.DeviceImpressionsBefore.Value + run.Batch.Impressions;

        run.Attempt.DeviceImpressionsAfter = counter;
        run.Attempt.DeviceJamEventsAfter = snapshot.JamEvents;
        run.Attempt.DeviceMispickEventsAfter = snapshot.MispickEvents;

        // Reaching the expected total is conclusive; stop waiting immediately.
        if (counter >= expected) return true;

        if (counter == run.LastCounterReading) run.StableCounterReads++;
        else { run.StableCounterReads = 0; run.LastCounterReading = counter; }

        // Two identical readings in a row, or the settle window expiring, ends the wait.
        if (run.StableCounterReads >= 2) return true;
        return now - run.LeftQueueUtc!.Value > CounterSettleWindow;
    }

    private async Task ConcludeAttemptAsync(ActiveRun run, CancellationToken ct)
    {
        var job = run.Job;
        var batch = run.Batch;
        var attempt = run.Attempt;
        var result = run.Result!;

        // One last look, so a job that is somehow still queued is not judged as finished.
        var stillQueued = _spooler.ListJobs(job.PrinterName, out var ok)
            .Any(j => string.Equals(j.Document, attempt.DocumentToken, StringComparison.Ordinal) &&
                      !j.IsRetained);

        if (result.SetupRejected)
        {
            await HandleRejectedSetupAsync(run, result).ConfigureAwait(false);
            return;
        }

        // The verifier compares printed sides: the spooler counts each side as a page, and the
        // printer's counter counts impressions, which are sides too.
        var verdict = BatchVerifier.Decide(new VerificationInput
        {
            ExpectedPages = batch.Impressions,
            SpooledSuccessfully = result.Spooled,
            FailedBeforeAnyOutput = result.FailedBeforeAnyOutput,
            ObservedInQueue = attempt.ObservedInQueue,
            ObservedPrinting = attempt.ObservedPrinting,
            SpoolerPagesPrinted = attempt.SpoolerPagesPrinted,
            SpoolerReportedPrintedFlag = run.SpoolerReportedPrintedFlag,
            SpoolerReportedError = run.SpoolerReportedError,
            JobStillInQueue = ok && stillQueued,
            DeviceImpressionDelta = attempt.DeviceImpressionDelta,
            DeviceJamDelta = attempt.DeviceJamDelta,
            DeviceMispickDelta = attempt.DeviceMispickDelta,
            SubmissionError = result.Error,
        });

        attempt.State = verdict.State;
        attempt.Evidence |= verdict.Evidence;
        attempt.VerdictExplanation = verdict.Explanation;
        attempt.FinishedUtc = _clock.UtcNow;
        attempt.Error = result.Error;
        _store.UpdateAttempt(attempt);

        if (result.ColourWarning is { } colourWarning)
        {
            Record(job, "Warning", "Printer",
                $"Batch {batch.Number} could not be guaranteed to print in black & white.", colourWarning);
        }

        // Advice about the layout (a margin inside the printer's unprintable edge) is the same
        // for every batch, so it is said once per job rather than after every batch.
        if (result.SetupWarning is { } setupWarning && _setupWarned.Add(job.Id))
            Record(job, "Warning", "Printer", "A print setting may not come out as intended.", setupWarning);

        _activeRun = null;
        run.Cancellation.Dispose();

        switch (verdict.State)
        {
            case AttemptState.Verified:
                batch.State = BatchState.Completed;
                batch.PagesConfirmedPrinted = batch.PageCount;
                batch.FinishedUtc = _clock.UtcNow;
                _store.UpdateBatch(batch);

                Record(job, "Information", "Batch",
                    $"Batch {batch.Number} completed ({batch.RangeLabel}).", verdict.Explanation);

                if (_settings.NotifyOnBatchComplete)
                {
                    NotifyUser(NotificationKind.Success,
                        $"Batch {batch.Number} of {job.BatchCount} printed",
                        $"{batch.RangeLabel} of {job.FileName}.", job);
                }

                await ContinueAfterBatchAsync(job).ConfigureAwait(false);
                break;

            case AttemptState.Failed:
                await HandleDefinitelyFailedBatchAsync(job, batch, verdict).ConfigureAwait(false);
                break;

            default:
            {
                var explanation = verdict.Explanation + PartialCountCaveat(job, batch, verdict.ConfirmedPages);
                batch.State = BatchState.NeedsAttention;
                batch.PagesConfirmedPrinted = job.Plan.UnitsProvenBy(batch.FirstPage, batch.LastPage, verdict.ConfirmedPages);
                batch.Note = explanation;
                _store.UpdateBatch(batch);

                job.AttentionReason = explanation;
                Record(job, "Warning", "Batch",
                    $"Batch {batch.Number} needs checking ({batch.RangeLabel}).", explanation);

                Transition(job, JobState.NeedsAttention, explanation);
                NotifyUser(NotificationKind.Warning,
                    $"Check batch {batch.Number} before continuing",
                    explanation, job);
                break;
            }
        }
    }

    /// <summary>
    /// For a sheet-based job, a part-way count of printed sides cannot say which sheets are
    /// complete - a printer that turns the paper over by hand prints every front first - so no
    /// resume point is offered, and the explanation says why.
    /// </summary>
    private static string PartialCountCaveat(PrintJob job, Batch batch, int confirmedSides)
    {
        if (confirmedSides <= 0 || confirmedSides >= batch.Impressions) return "";
        if (job.Plan.UnitsProvenBy(batch.FirstPage, batch.LastPage, confirmedSides) > 0) return "";
        return $" This batch prints {batch.Impressions} sides on {batch.PageCount} sheet(s), and a count of printed sides " +
               "cannot tell which sheets are complete, so check the sheets themselves.";
    }

    /// <summary>
    /// The driver would not take the job's sides, paper or orientation, so nothing was sent.
    /// Sending it again would fail the same way, so the job waits - with its settings and every
    /// batch intact - for the user to fix the printer and press Resume, or cancel.
    /// </summary>
    private async Task HandleRejectedSetupAsync(ActiveRun run, PrintResult result)
    {
        var job = run.Job;
        var batch = run.Batch;
        var attempt = run.Attempt;

        attempt.State = AttemptState.Failed;
        attempt.FinishedUtc = _clock.UtcNow;
        attempt.Error = result.Error;
        attempt.VerdictExplanation = "Nothing was sent: the printer driver would not accept this job's print settings.";
        _store.UpdateAttempt(attempt);

        _activeRun = null;
        run.Cancellation.Dispose();

        batch.State = BatchState.Pending;
        batch.Note = result.Error;
        _store.UpdateBatch(batch);

        var reason = $"{result.Error} Batch {batch.Number} ({batch.RangeLabel}) was not sent. " +
                     "Fix the printer settings and press Resume, or cancel the job.";
        job.AttentionReason = reason;
        job.LastError = result.Error;

        Record(job, "Error", "Printer", $"Batch {batch.Number} was not sent: the printer would not accept the print settings.",
            result.Error);
        Transition(job, JobState.NeedsAttention, reason);
        NotifyUser(NotificationKind.Problem, "Printer settings not accepted", reason, job);
        ReleasePowerIfIdle();

        await Task.CompletedTask;
    }

    private async Task HandleDefinitelyFailedBatchAsync(PrintJob job, Batch batch, VerificationVerdict verdict)
    {
        var attempts = batch.Attempts.Count;

        if (attempts >= Math.Max(1, _settings.MaxAutoRetries))
        {
            batch.State = BatchState.Failed;
            batch.Note = verdict.Explanation;
            _store.UpdateBatch(batch);

            var reason = $"Batch {batch.Number} ({batch.RangeLabel}) did not print after {attempts} attempts. " +
                         verdict.Explanation;

            job.LastError = reason;
            job.AttentionReason = reason;
            Record(job, "Error", "Batch", $"Batch {batch.Number} failed after {attempts} attempts.",
                verdict.Explanation);

            Transition(job, JobState.NeedsAttention, reason);
            NotifyUser(NotificationKind.Problem, $"Batch {batch.Number} failed", reason, job);
            return;
        }

        // Confirmed that nothing printed, and we have retries left. Wait out the retry
        // interval first so we do not hammer a printer that is simply switched off.
        batch.State = BatchState.Pending;
        batch.Note = verdict.Explanation;
        _store.UpdateBatch(batch);

        var wait = TimeSpan.FromMinutes(Math.Max(1, _settings.RetryIntervalMinutes));
        SetDeadline(job, _clock.UtcNow + wait, wait);

        Record(job, "Warning", "Batch",
            $"Batch {batch.Number} did not print; retrying in {wait.TotalMinutes:0} minutes " +
            $"(attempt {attempts + 1} of {_settings.MaxAutoRetries}).", verdict.Explanation);

        Transition(job, JobState.BlockedPrinterOffline,
            $"Nothing printed. Trying again in {Deadline.Describe(wait)}.");

        await Task.CompletedTask;
    }

    private async Task ContinueAfterBatchAsync(PrintJob job)
    {
        var remaining = job.Batches.Count(b => b.State == BatchState.Pending);
        if (remaining == 0)
        {
            var unresolved = job.Batches.Count(b => b.State is BatchState.NeedsAttention or BatchState.Failed);
            if (unresolved > 0)
            {
                var reason = $"{unresolved} batch(es) still need checking before this job can be finished.";
                job.AttentionReason = reason;
                Transition(job, JobState.NeedsAttention, reason);
                return;
            }

            CompleteJob(job);
            return;
        }

        if (job.State == JobState.Paused)
        {
            Transition(job, JobState.Paused, "Paused. Nothing further will be sent until you resume.");
            return;
        }

        // Resolved by hand while the queue had moved on to another job: finish that job first.
        if (PrinterHolder(except: job) is { } holder)
        {
            ParkInQueue(job, holder);
            return;
        }

        var delay = TimeSpan.FromMinutes(Math.Max(0, job.DelayMinutes));
        if (delay <= TimeSpan.Zero)
        {
            await TryStartNextBatchAsync(job).ConfigureAwait(false);
            return;
        }

        var due = _clock.UtcNow + delay;
        SetDeadline(job, due, delay);
        job.NextBatchDueUtc = due;

        var next = job.NextPendingBatch;
        Record(job, "Information", "Schedule",
            $"Resting the printer for {delay.TotalMinutes:0} minutes.",
            next is null ? null : $"Next: batch {next.Number} ({next.RangeLabel}) at {due.ToLocalTime():HH:mm}.");

        Transition(job, JobState.CoolingDown,
            next is null
                ? $"Waiting {Deadline.Describe(delay)}."
                : $"Resting the printer. Batch {next.Number} ({next.RangeLabel}) starts at {due.ToLocalTime():HH:mm}.");

        if (_settings.NotifyOnBatchComplete && next is not null)
        {
            NotifyUser(NotificationKind.Information, "Next batch scheduled",
                $"Batch {next.Number} ({next.RangeLabel}) starts at {due.ToLocalTime():HH:mm}.", job);
        }

        ReleasePowerIfIdle();
    }

    private void CompleteJob(PrintJob job)
    {
        job.FinishedUtc = _clock.UtcNow;
        job.NextBatchDueUtc = null;
        ClearDeadline(job);

        var done = job.Batches.Where(b => b.State is BatchState.Completed or BatchState.Skipped).ToList();
        var pages = done.SelectMany(b => b.SourcePages).Distinct().Count();
        var sheets = done.Sum(b => b.PageCount);
        var what = job.Plan.Unit == PrintUnit.Page ? $"{pages} pages" : $"{pages} pages, on {sheets} sheets,";

        Record(job, "Information", "Job", $"Job complete: all {what} of {job.FileName} printed.",
            job.PrintSettings == PrintSettings.Default ? null : job.PrintSettings.Summary() + ".");
        Transition(job, JobState.Completed, job.Plan.Unit == PrintUnit.Page
            ? $"All {pages} pages printed."
            : $"All {sheets} sheets printed ({pages} pages).");

        if (_settings.NotifyOnJobComplete)
        {
            NotifyUser(NotificationKind.Success, "Printing finished",
                $"All {what.TrimEnd(',')} of {job.FileName} have been printed.", job);
        }

        var waiting = _jobs.Count(j => j.State == JobState.Queued);
        if (waiting > 0)
        {
            Record(job, "Information", "Queue", $"{job.Label} finished; {waiting} job(s) still waiting in the queue.",
                job.DelayMinutes > 0
                    ? $"The next job on \"{job.PrinterName}\" starts after the printer rests {job.DelayMinutes} min."
                    : null);
        }

        if (job.InvoiceState == InvoiceState.Waiting)
        {
            // Another job's batch on the same printer would count the invoice page as its own.
            if (_activeRun is { } run && run.Job != job &&
                string.Equals(run.Job.PrinterName, job.PrinterName, StringComparison.OrdinalIgnoreCase))
            {
                Record(job, "Information", "Invoice", "The invoice will print once the batch now printing has finished.");
            }
            else
            {
                BeginInvoicePrint(job);
            }
        }

        ReleasePowerIfIdle();
    }

    // ---- Invoice -------------------------------------------------------------

    /// <summary>
    /// Prints the invoice for a finished job on request: to reprint one, to retry one the
    /// printer refused, or to bill for the pages of a job that was cancelled part-way.
    /// </summary>
    public void PrintInvoice(string jobId) => Post(_ =>
    {
        var job = Find(jobId);
        if (job is { IsTerminal: true, WantsInvoice: true } && job.InvoiceState != InvoiceState.Sending)
            BeginInvoicePrint(job);
        return Task.CompletedTask;
    });

    /// <summary>
    /// Sends the invoice as its own one-page print job, after the last batch has been
    /// verified, so it can never be mistaken for part of a batch by the page-counter check.
    /// </summary>
    private void BeginInvoicePrint(PrintJob job)
    {
        var invoice = Invoice.For(job, _clock.UtcNow);

        // Recorded before the printer is asked, for the same reason as a batch's submit
        // intent: if we stop mid-send, the record says "may have printed", not "not yet".
        job.InvoiceState = InvoiceState.Sending;
        _store.UpdateJob(job);
        Notify(job);

        Record(job, "Information", "Invoice", $"Printing invoice {invoice.Number}.",
            $"{invoice.BlackWhitePages} black & white, {invoice.ColourPages} colour, total {invoice.Total}.");

        Task<PrintResult> printing;
        try
        {
            printing = _invoicePrinter.PrintAsync(invoice, job.PrinterName, _shutdown.Token);
        }
        catch (Exception ex)
        {
            printing = Task.FromResult(PrintResult.Failure(ex.Message, _clock.UtcNow, beforeAnyOutput: true));
        }

        var jobId = job.Id;
        _ = printing.ContinueWith(
            finished => Post(_ =>
            {
                FinishInvoicePrint(jobId, finished);
                return Task.CompletedTask;
            }),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void FinishInvoicePrint(string jobId, Task<PrintResult> finished)
    {
        var job = Find(jobId);
        if (job is null || job.InvoiceState != InvoiceState.Sending) return;

        var result = finished.IsCompletedSuccessfully
            ? finished.Result
            : PrintResult.Failure(finished.Exception?.GetBaseException().Message ?? "The invoice was cancelled.",
                _clock.UtcNow, beforeAnyOutput: false);

        if (result.Spooled)
        {
            job.InvoiceState = InvoiceState.Printed;
            job.InvoicePrintedUtc = _clock.UtcNow;
            _store.UpdateJob(job);
            Record(job, "Information", "Invoice", "Invoice printed.");
        }
        else
        {
            // A failure before any output means nothing can be in the tray; anything else
            // might be, and the user is told so rather than having it resent for them.
            job.InvoiceState = result.FailedBeforeAnyOutput ? InvoiceState.Failed : InvoiceState.Interrupted;
            _store.UpdateJob(job);
            Record(job, "Warning", "Invoice", "The invoice could not be printed.", result.Error);
            NotifyUser(NotificationKind.Warning, "Invoice not printed",
                $"The invoice for {job.FileName} did not print. Open {AppPaths.ProductName} to print it again.", job);
        }

        Notify(job);
    }

    // ---- Printer readiness ---------------------------------------------------

    private sealed record Readiness(bool Ready, string StatusText, bool IsError, string? Detail);

    private async Task<Readiness> CheckPrinterAsync(PrintJob job, CancellationToken ct)
    {
        var snapshot = _spooler.GetPrinter(job.PrinterName);

        if (!snapshot.Exists)
        {
            return new Readiness(false,
                $"The printer \"{job.PrinterName}\" is no longer installed in Windows.", true,
                "Reconnect the printer, or pick a different one in the job's settings.");
        }

        switch (snapshot.Availability)
        {
            case PrinterAvailability.Error:
                return new Readiness(false, snapshot.StatusText, true, null);
            case PrinterAvailability.Offline:
                return new Readiness(false, "The printer is offline.", false, snapshot.StatusText);
            case PrinterAvailability.Paused:
                return new Readiness(false,
                    snapshot.WorkOffline
                        ? "The printer is set to \"Use Printer Offline\" in Windows."
                        : "The Windows print queue is paused.",
                    false, snapshot.StatusText);
            case PrinterAvailability.NotFound:
                return new Readiness(false, "The printer was removed from Windows.", true, null);
        }

        // The device probe may only ADD a reason to hold back, never remove one. A network
        // printer that is asleep will not answer, and that must not be mistaken for a fault.
        var probe = await _probes.GetProbeAsync(job.PrinterName, ct).ConfigureAwait(false);
        if (probe.IsConfigured)
        {
            try
            {
                var device = await probe.ProbeAsync(ct).ConfigureAwait(false);
                if (device.Reachable)
                {
                    if (device.State == DeviceState.Stopped)
                    {
                        var reasons = device.StateReasons.Count > 0
                            ? string.Join(", ", device.StateReasons)
                            : "the printer reports it is stopped";
                        return new Readiness(false, $"The printer needs attention: {reasons}.", true,
                            device.StateMessage);
                    }

                    if (device.AcceptingJobs == false)
                        return new Readiness(false, "The printer is not accepting jobs.", true, device.Describe());
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Device probe failed during readiness check");
            }
        }

        return new Readiness(true, snapshot.StatusText, false, null);
    }

    private void EnterBlocked(PrintJob job, Readiness readiness)
    {
        job.ConsecutiveBlockedChecks++;

        var wait = TimeSpan.FromMinutes(Math.Max(1, _settings.RetryIntervalMinutes));
        SetDeadline(job, _clock.UtcNow + wait, wait);

        var state = readiness.IsError ? JobState.BlockedPrinterError : JobState.BlockedPrinterOffline;
        var message = $"{readiness.StatusText} Checking again in {Deadline.Describe(wait)}.";

        // Escalate to a human if the printer has been unusable for a long time, rather than
        // silently retrying until morning.
        if (_settings.BlockedGiveUpMinutes > 0)
        {
            var blockedFor = job.ConsecutiveBlockedChecks * wait;
            if (blockedFor >= TimeSpan.FromMinutes(_settings.BlockedGiveUpMinutes))
            {
                var reason = $"The printer has been unavailable for {blockedFor.TotalHours:0.#} hours " +
                             $"({readiness.StatusText}). Nothing has been sent.";
                job.AttentionReason = reason;
                ClearDeadline(job);
                Transition(job, JobState.NeedsAttention, reason);
                NotifyUser(NotificationKind.Problem, "Printer still unavailable", reason, job);
                return;
            }
        }

        // Log and notify on the first block and then sparingly, not every five minutes.
        var shouldAnnounce = job.ConsecutiveBlockedChecks == 1 ||
                             job.ConsecutiveBlockedChecks % 6 == 0;

        if (shouldAnnounce)
        {
            Record(job, "Warning", "Printer", readiness.StatusText, readiness.Detail);

            if (_settings.NotifyOnProblem && ShouldNotifyBlocked(job))
                NotifyUser(NotificationKind.Warning, "Printer unavailable", message, job);
        }

        Transition(job, state, message);
    }

    private bool ShouldNotifyBlocked(PrintJob job)
    {
        var now = _clock.UtcNow;
        if (_lastBlockedNotice.TryGetValue(job.Id, out var last) && now - last < TimeSpan.FromMinutes(30))
            return false;
        _lastBlockedNotice[job.Id] = now;
        return true;
    }

    private async Task RetryAfterBlockAsync(PrintJob job, CancellationToken ct)
    {
        var readiness = await CheckPrinterAsync(job, ct).ConfigureAwait(false);
        job.LastPrinterStatus = readiness.StatusText;

        if (!readiness.Ready)
        {
            EnterBlocked(job, readiness);
            return;
        }

        _lastBlockedNotice.Remove(job.Id);
        Record(job, "Information", "Printer", "The printer is available again; continuing.");
        await TryStartNextBatchAsync(job).ConfigureAwait(false);
    }

    private async Task<DeviceCounters?> ReadCountersAsync(PrintJob job, CancellationToken ct)
    {
        try
        {
            var probe = await _probes.GetProbeAsync(job.PrinterName, ct).ConfigureAwait(false);
            if (!probe.IsConfigured) return null;

            var snapshot = await probe.ProbeAsync(ct).ConfigureAwait(false);
            if (!snapshot.HasVerificationCounter) return null;

            return new DeviceCounters(snapshot.VerificationCounter, snapshot.JamEvents, snapshot.MispickEvents);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read device counters");
            return null;
        }
    }

    // ---- Cancellation of in-flight work --------------------------------------

    private async Task AbandonActiveRunAsync(string why)
    {
        if (_activeRun is not { } run) return;

        await CancelSpoolerJobAsync(run, why).ConfigureAwait(false);

        run.Cancellation.Cancel();
        try { run.Result ??= await run.Submission.ConfigureAwait(false); }
        catch (Exception ex) { Log.Debug(ex, "Submission task faulted while abandoning the run"); }

        run.Attempt.State = AttemptState.Cancelled;
        run.Attempt.FinishedUtc = _clock.UtcNow;
        run.Attempt.VerdictExplanation = $"Abandoned because {why}.";
        _store.UpdateAttempt(run.Attempt);

        run.Cancellation.Dispose();
        _activeRun = null;
    }

    private async Task CancelSpoolerJobAsync(ActiveRun run, string why)
    {
        var jobId = run.Attempt.SpoolerJobId;

        // The id is discovered a moment after submission, so a cancel issued in that window
        // would otherwise find nothing to cancel and let the batch print anyway - after the
        // user had been told it was stopped. Fall back to finding the job by its document
        // token, which is unique to this attempt.
        if (jobId is null)
        {
            var queued = _spooler.ListJobs(run.Job.PrinterName, out var querySucceeded);
            if (querySucceeded)
            {
                jobId = queued
                    .FirstOrDefault(j => string.Equals(j.Document, run.Attempt.DocumentToken,
                        StringComparison.Ordinal))?.JobId;

                if (jobId is not null)
                {
                    run.Attempt.SpoolerJobId = jobId;
                    _store.UpdateAttempt(run.Attempt);
                }
            }
        }

        if (jobId is null)
        {
            Record(run.Job, "Information", "Printer",
                $"No Windows print job was found for batch {run.Batch.Number} to cancel.",
                "It had not reached the queue, or had already left it.");
            return;
        }

        if (_spooler.CancelJob(run.Job.PrinterName, jobId.Value, out var error))
        {
            Record(run.Job, "Information", "Printer",
                $"Cancelled the Windows print job for batch {run.Batch.Number} because {why}.");
        }
        else
        {
            Record(run.Job, "Warning", "Printer",
                $"Could not cancel the Windows print job for batch {run.Batch.Number}.", error);
        }

        await Task.CompletedTask;
    }

    // ---- Recovery ------------------------------------------------------------

    /// <summary>
    /// Works out, for every unfinished job, what state the world was left in.
    ///
    /// The dangerous case is an attempt that was mid-flight when the process died. It is
    /// resolved by looking for the attempt's unique document token in the Windows queue:
    ///
    ///   - still there  -> the job survived; re-attach and keep monitoring it.
    ///   - gone         -> ask the printer's page counter what physically happened.
    ///   - no counter   -> declare it ambiguous and ask the user. Never assume.
    /// </summary>
    private async Task<IReadOnlyList<RecoveryReport>> RecoverAsync()
    {
        var reports = new List<RecoveryReport>();

        // Drafts and queued jobs have nothing in flight: they simply keep waiting, in order.
        foreach (var job in _jobs.Where(j => !j.IsTerminal && j.State is not (JobState.Draft or JobState.Queued)))
        {
            var details = new List<string>();
            var needsDecision = false;

            var openAttempts = job.Batches
                .SelectMany(b => b.Attempts.Select(a => (Batch: b, Attempt: a)))
                .Where(x => x.Attempt.State is AttemptState.SubmitPending or AttemptState.Submitted
                            or AttemptState.Spooling or AttemptState.Printing or AttemptState.LeftQueue)
                .ToList();

            // A batch we adopted on a previous restart may have finished in the meantime.
            // If the printer's physical counter now accounts for it exactly, we can settle
            // the question without troubling the user.
            foreach (var batch in job.Batches.Where(b => b.State == BatchState.NeedsAttention).ToList())
            {
                if (await TrySettleAdoptedBatchAsync(job, batch).ConfigureAwait(false))
                    details.Add($"Batch {batch.Number} ({batch.RangeLabel}) finished while " +
                                $"{AppPaths.ProductName} was closed; confirmed by the printer's page counter.");
            }

            foreach (var (batch, attempt) in openAttempts)
            {
                var outcome = await ResolveInterruptedAttemptAsync(job, batch, attempt).ConfigureAwait(false);
                details.Add(outcome.Detail);
                needsDecision |= outcome.NeedsDecision;
            }

            needsDecision |= job.Batches.Any(b => b.State is BatchState.NeedsAttention or BatchState.Failed);

            // Any batch left marked in-flight with no open attempt is bookkeeping debris.
            foreach (var batch in job.Batches.Where(b => b.State == BatchState.InFlight))
            {
                batch.State = BatchState.NeedsAttention;
                batch.Note ??= "The application stopped while this batch was in progress.";
                _store.UpdateBatch(batch);
                needsDecision = true;
            }

            if (needsDecision)
            {
                job.AttentionReason ??= "One or more batches were interrupted and need checking.";
                job.State = JobState.NeedsAttention;
            }
            else
            {
                RestoreSchedule(job);
            }

            _store.UpdateJob(job);

            var summary = needsDecision
                ? "Interrupted while printing; please confirm what came out before continuing."
                : DescribeResumePoint(job);

            reports.Add(new RecoveryReport(job.Id, job.FileName, needsDecision, summary, details));

            Record(job, needsDecision ? "Warning" : "Information", "Recovery",
                $"Recovered unfinished job for {job.FileName}.", summary);
        }

        var queued = _jobs.Count(j => j.State == JobState.Queued);
        if (queued > 0)
        {
            Record(null, "Information", "Recovery",
                $"{queued} job(s) still waiting in the print queue; they will print in order.");
        }

        // After the loop above, which can itself find that a job finished while we were closed.
        RecoverInvoices();

        if (reports.Count > 0)
        {
            Log.Information("Recovered {Count} unfinished job(s); {Attention} need a decision",
                reports.Count, reports.Count(r => r.NeedsUserDecision));
        }

        return reports;
    }

    /// <summary>
    /// Settles invoices of finished jobs that the last session did not see through.
    ///
    /// One caught mid-send may already be in the tray, so it is left for the user to reprint.
    /// One still waiting was never sent at all - we stopped between finishing the last batch
    /// and starting the invoice - so it is safe to send now.
    /// </summary>
    private void RecoverInvoices()
    {
        foreach (var job in _jobs.Where(j => j.State == JobState.Completed))
        {
            switch (job.InvoiceState)
            {
                case InvoiceState.Sending:
                    job.InvoiceState = InvoiceState.Interrupted;
                    _store.UpdateJob(job);
                    Record(job, "Warning", "Invoice",
                        $"{AppPaths.ProductName} stopped while the invoice was being sent.",
                        "Check the printer's tray, and print it again from the job screen if it is not there.");
                    break;

                case InvoiceState.Waiting:
                    BeginInvoicePrint(job);
                    break;
            }
        }
    }

    /// <summary>
    /// Re-examines a batch that was left needing attention because its print job was still
    /// in the Windows queue when the application stopped.
    ///
    /// If that job has since left the queue and the printer's physical counter has advanced
    /// by exactly the batch's page count, the batch demonstrably printed and there is
    /// nothing to ask about.
    ///
    /// This only ever moves a batch towards "completed". It can never cause a resend, so
    /// the worst case of a wrong call here is that the user is not asked about pages that
    /// really did come out. The rule is deliberately narrow: it applies only to jobs this
    /// application watched and adopted, never to a batch whose outcome was unexplained.
    /// </summary>
    private async Task<bool> TrySettleAdoptedBatchAsync(PrintJob job, Batch batch)
    {
        var attempt = batch.LatestAttempt;
        if (attempt is null) return false;
        if (!attempt.Evidence.HasFlag(CompletionEvidence.ReattachedAfterRestart)) return false;
        if (attempt.DeviceImpressionsBefore is not { } before) return false;

        var queued = _spooler.ListJobs(attempt.PrinterName, out var querySucceeded);
        if (!querySucceeded) return false;

        var stillActive = queued.Any(j =>
            string.Equals(j.Document, attempt.DocumentToken, StringComparison.Ordinal) && !j.IsCompleted);
        if (stillActive) return false;

        var counters = await ReadCountersAsync(job, CancellationToken.None).ConfigureAwait(false);
        if (counters?.Impressions is not { } after) return false;
        if (after - before != batch.Impressions) return false;

        attempt.DeviceImpressionsAfter = after;
        attempt.DeviceJamEventsAfter = counters.Value.JamEvents;
        attempt.DeviceMispickEventsAfter = counters.Value.MispickEvents;
        attempt.State = AttemptState.Verified;
        attempt.Evidence |= CompletionEvidence.DeviceCounterMatched;
        attempt.FinishedUtc ??= _clock.UtcNow;
        attempt.VerdictExplanation =
            $"The printer's page counter advanced by exactly {batch.Impressions} while " +
            $"{AppPaths.ProductName} was closed, which confirms this batch printed.";
        _store.UpdateAttempt(attempt);

        batch.State = BatchState.Completed;
        batch.PagesConfirmedPrinted = batch.PageCount;
        batch.FinishedUtc ??= _clock.UtcNow;
        batch.Note = attempt.VerdictExplanation;
        _store.UpdateBatch(batch);

        RestPrinterAfterRecoveredBatch(job);

        Record(job, "Information", "Recovery",
            $"Batch {batch.Number} ({batch.RangeLabel}) confirmed printed after the interruption.",
            attempt.VerdictExplanation);

        return true;
    }

    /// <summary>
    /// Gives the printer its configured rest after a batch that recovery has just found to
    /// have printed.
    ///
    /// We cannot know when that batch actually finished - the application was not running -
    /// so the only safe assumption is that it finished moments ago. Starting the next batch
    /// immediately would defeat the entire purpose of the application on the one occasion
    /// the printer is most likely to be hot.
    /// </summary>
    private void RestPrinterAfterRecoveredBatch(PrintJob job)
    {
        if (job.DelayMinutes <= 0) return;
        if (job.NextPendingBatch is null) return;

        var delay = TimeSpan.FromMinutes(job.DelayMinutes);
        job.NextBatchDueUtc = _clock.UtcNow + delay;
    }

    private sealed record AttemptRecovery(bool NeedsDecision, string Detail);

    private async Task<AttemptRecovery> ResolveInterruptedAttemptAsync(PrintJob job, Batch batch, BatchAttempt attempt)
    {
        var queued = _spooler.ListJobs(attempt.PrinterName, out var querySucceeded);
        var stillThere = querySucceeded
            ? queued.FirstOrDefault(j => string.Equals(j.Document, attempt.DocumentToken, StringComparison.Ordinal))
            : null;

        if (stillThere is not null)
        {
            // The spooler outlived us. Adopt the job rather than creating a second one.
            attempt.SpoolerJobId = stillThere.JobId;
            attempt.SpoolerPagesPrinted = Math.Max(attempt.SpoolerPagesPrinted, stillThere.PagesPrinted);
            attempt.Evidence |= CompletionEvidence.ReattachedAfterRestart;
            attempt.State = AttemptState.Ambiguous;
            attempt.VerdictExplanation =
                $"This batch was still in the Windows queue when {AppPaths.ProductName} restarted " +
                $"({stillThere.PagesPrinted} of {batch.Impressions} pages reported). " +
                "Let it finish, or cancel it, then confirm what printed.";
            _store.UpdateAttempt(attempt);

            batch.State = BatchState.NeedsAttention;
            batch.Note = attempt.VerdictExplanation;
            batch.PagesConfirmedPrinted = job.Plan.UnitsProvenBy(batch.FirstPage, batch.LastPage, stillThere.PagesPrinted);
            _store.UpdateBatch(batch);

            return new AttemptRecovery(true,
                $"Batch {batch.Number} ({batch.RangeLabel}) is still in the Windows print queue.");
        }

        // Not in the queue. Ask the hardware what happened.
        var counters = await ReadCountersAsync(job, CancellationToken.None).ConfigureAwait(false);
        if (counters?.Impressions is { } after && attempt.DeviceImpressionsBefore is not null)
        {
            attempt.DeviceImpressionsAfter = after;
            attempt.DeviceJamEventsAfter = counters.Value.JamEvents;
            attempt.DeviceMispickEventsAfter = counters.Value.MispickEvents;
        }

        var verdict = BatchVerifier.Decide(new VerificationInput
        {
            ExpectedPages = batch.Impressions,
            // We cannot know whether spooling completed, because we died during it.
            SpooledSuccessfully = attempt.State != AttemptState.SubmitPending,
            FailedBeforeAnyOutput = false,
            ObservedInQueue = attempt.ObservedInQueue,
            ObservedPrinting = attempt.ObservedPrinting,
            SpoolerPagesPrinted = attempt.SpoolerPagesPrinted,
            SpoolerReportedPrintedFlag = false,
            SpoolerReportedError = false,
            JobStillInQueue = false,
            DeviceImpressionDelta = attempt.DeviceImpressionDelta,
            DeviceJamDelta = attempt.DeviceJamDelta,
            DeviceMispickDelta = attempt.DeviceMispickDelta,
            SubmissionError = null,
        });

        attempt.State = verdict.State;
        attempt.Evidence |= verdict.Evidence;
        attempt.VerdictExplanation = verdict.Explanation;
        attempt.FinishedUtc ??= _clock.UtcNow;
        _store.UpdateAttempt(attempt);

        switch (verdict.State)
        {
            case AttemptState.Verified:
                batch.State = BatchState.Completed;
                batch.PagesConfirmedPrinted = batch.PageCount;
                batch.FinishedUtc ??= _clock.UtcNow;
                _store.UpdateBatch(batch);
                RestPrinterAfterRecoveredBatch(job);
                return new AttemptRecovery(false,
                    $"Batch {batch.Number} ({batch.RangeLabel}) printed before the interruption. {verdict.Explanation}");

            case AttemptState.Failed:
                batch.State = BatchState.Pending;
                batch.Note = verdict.Explanation;
                _store.UpdateBatch(batch);
                return new AttemptRecovery(false,
                    $"Batch {batch.Number} ({batch.RangeLabel}) did not print and will be sent again. " +
                    verdict.Explanation);

            default:
                batch.State = BatchState.NeedsAttention;
                batch.PagesConfirmedPrinted = job.Plan.UnitsProvenBy(batch.FirstPage, batch.LastPage, verdict.ConfirmedPages);
                batch.Note = verdict.Explanation + PartialCountCaveat(job, batch, verdict.ConfirmedPages);
                _store.UpdateBatch(batch);
                return new AttemptRecovery(true,
                    $"Batch {batch.Number} ({batch.RangeLabel}): {verdict.Explanation}");
        }
    }

    /// <summary>Puts a cleanly recovered job back on its schedule without losing elapsed rest time.</summary>
    private void RestoreSchedule(PrintJob job)
    {
        if (job.NextPendingBatch is null)
        {
            if (job.Batches.All(b => b.State is BatchState.Completed or BatchState.Skipped))
            {
                job.State = JobState.Completed;
                job.FinishedUtc ??= _clock.UtcNow;
            }
            return;
        }

        var now = _clock.UtcNow;

        if (job.State == JobState.Paused) return;

        if (job.NextBatchDueUtc is { } due && due > now)
        {
            SetDeadline(job, due, due - now);
            job.State = JobState.CoolingDown;
            return;
        }

        if (job.State == JobState.WaitingForStart && job.ScheduledStartUtc is { } start && start > now)
        {
            SetDeadline(job, start, start - now);
            return;
        }

        // Due already, or was mid-flight: start as soon as the loop runs, subject to the
        // usual printer-readiness checks.
        if (_settings.AutoResumeUnambiguousJobs)
        {
            job.NextBatchDueUtc = now;
            SetDeadline(job, now, TimeSpan.Zero);
            job.State = JobState.CoolingDown;
        }
        else
        {
            job.State = JobState.Paused;
            job.AttentionReason = "Automatic resume is switched off. Press Resume to continue.";
        }
    }

    private string DescribeResumePoint(PrintJob job)
    {
        var done = job.Batches.Count(b => b.State is BatchState.Completed or BatchState.Skipped);
        var next = job.NextPendingBatch;
        return next is null
            ? $"All batches accounted for ({done} of {job.BatchCount})."
            : $"{done} of {job.BatchCount} batches done. Will continue with batch {next.Number} ({next.RangeLabel}).";
    }

    // ---- Small helpers --------------------------------------------------------

    private PrintJob? Find(string jobId) => _jobs.FirstOrDefault(j => j.Id == jobId);

    private static int NextFreeBatchNumber(PrintJob job) => job.Batches.Max(b => b.Number) + 1;

    private void Post(Func<CancellationToken, Task> command) => _commands.Writer.TryWrite(command);

    private void SetDeadline(PrintJob job, DateTimeOffset dueUtc, TimeSpan plannedDuration)
    {
        _deadlines[job.Id] = new Deadline(_clock, dueUtc, plannedDuration);
        job.NextBatchDueUtc = dueUtc;
    }

    private void ClearDeadline(PrintJob job)
    {
        _deadlines.Remove(job.Id);
        job.NextBatchDueUtc = null;
    }

    private void Transition(PrintJob job, JobState state, string? note)
    {
        var previous = job.State;
        job.State = state;
        if (note is not null) job.LastPrinterStatus = note;
        if (state != JobState.NeedsAttention) _setAside.Remove(job.Id);

        _store.UpdateJob(job);

        if (previous != state)
            Log.Information("{Label} {Job} {From} -> {To}: {Note}", job.Label, job.FileName, previous, state, note);

        Notify(job);
    }

    /// <summary>
    /// Publishes the job's current state. The snapshot is built here, on the engine loop,
    /// so that whatever the subscriber does with it cannot observe a half-applied change.
    /// </summary>
    private void Notify(PrintJob job)
    {
        if (JobChanged is not { } handler) return;

        var snapshot = JobSnapshot.From(job);
        try
        {
            handler(snapshot);
        }
        catch (Exception ex)
        {
            // A misbehaving subscriber must never take the scheduler down with it.
            Log.Error(ex, "A job-changed subscriber threw");
        }
    }

    private void Record(PrintJob? job, string level, string category, string message, string? detail = null)
    {
        _store.AppendEvent(job?.Id, level, category, message, detail);
        EventAppended?.Invoke(new JobEvent(0, job?.Id, _clock.UtcNow, level, category, message, detail));

        // The log file interleaves every job in the queue, so each line says which job it is.
        var who = job is null ? "Queue" : $"{job.Label} {job.FileName}";
        switch (level)
        {
            case "Error": Log.Error("[{Who}] {Category}: {Message} {Detail}", who, category, message, detail); break;
            case "Warning": Log.Warning("[{Who}] {Category}: {Message} {Detail}", who, category, message, detail); break;
            default: Log.Information("[{Who}] {Category}: {Message}", who, category, message); break;
        }
    }

    private void NotifyUser(NotificationKind kind, string title, string message, PrintJob? job)
    {
        if (!_settings.NotificationsEnabled) return;
        if (kind is NotificationKind.Problem or NotificationKind.Warning && !_settings.NotifyOnProblem) return;

        _notifier.Notify(new Notification(kind, title, message, job?.Id));
    }

    private void ReleasePowerIfIdle()
    {
        if (!_power.IsHoldingRequest) return;

        // The hold is only needed while something is actually in flight or a countdown is
        // running. A cooling-down job still needs it: the machine must be awake to send the
        // next batch.
        var needed = _activeRun is not null ||
                     _jobs.Any(j => j.State is JobState.CoolingDown or JobState.WaitingForStart
                                        or JobState.PrintingBatch or JobState.VerifyingBatch
                                        or JobState.BlockedPrinterOffline or JobState.BlockedPrinterError) ||
                     QueueWillMoveByItself();

        if (!needed) _power.Release();
    }

    /// <summary>
    /// Whether queued jobs will start without anyone touching anything: nothing is holding the
    /// printer for the user - a paused job, or one waiting on a decision the queue may not pass.
    /// </summary>
    private bool QueueWillMoveByItself()
    {
        if (!_jobs.Any(j => j.State == JobState.Queued)) return false;
        return PrinterHolder() switch
        {
            null => true,
            { State: JobState.Paused } => false,
            { State: JobState.NeedsAttention } => _settings.ContinueQueueWhenJobNeedsDecision,
            _ => true,
        };
    }

    /// <summary>
    /// Stops the loop abruptly, performing none of the tidy-up that <see cref="DisposeAsync"/>
    /// does. Used by the test suite to model a crash or a power cut, where only what has
    /// already been committed to the database survives.
    /// </summary>
    public async Task SimulateAbruptTerminationAsync()
    {
        _shutdown.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (Exception) { /* a crash does not report errors */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (Exception ex) { Log.Debug(ex, "Engine loop ended with an error"); }
        }

        // A batch already spooling is deliberately left to finish: killing it would create
        // exactly the ambiguity this application exists to avoid.
        if (_activeRun is { } run)
        {
            run.Attempt.State = AttemptState.Submitted;
            run.Attempt.VerdictExplanation =
                $"{AppPaths.ProductName} was closed while this batch was in the printer's queue.";
            _store.UpdateAttempt(run.Attempt);
        }

        _power.Release();
        _shutdown.Dispose();
    }

    /// <summary>State for the one batch currently in flight. Deliberately not persisted.</summary>
    private sealed class ActiveRun
    {
        public required PrintJob Job { get; init; }
        public required Batch Batch { get; init; }
        public required BatchAttempt Attempt { get; init; }
        public required Task<PrintResult> Submission { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public required DateTimeOffset StartedUtc { get; init; }

        public PrintResult? Result { get; set; }
        public DateTimeOffset LastProgressUtc { get; set; }
        public DateTimeOffset? LeftQueueUtc { get; set; }
        public DateTimeOffset? NextCounterReadUtc { get; set; }
        public DateTimeOffset? NextSpoolerPollUtc { get; set; }
        public long? LastCounterReading { get; set; }
        public int StableCounterReads { get; set; }
        public bool SawJobInQueue { get; set; }
        public bool SpoolerReportedPrintedFlag { get; set; }
        public bool SpoolerReportedError { get; set; }
        public bool StallReported { get; set; }
        public int ConsecutiveQueryFailures { get; set; }
    }
}
