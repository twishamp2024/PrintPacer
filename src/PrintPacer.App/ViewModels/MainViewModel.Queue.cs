using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using PrintPacer.Core.Billing;
using PrintPacer.Core.Engine;
using PrintPacer.Core.Model;
using PrintPacer.Core.Pdf;

namespace PrintPacer.App.ViewModels;

/// <summary>
/// The print queue: many PDFs, each its own job with its own settings.
///
/// Adding files creates draft jobs from the defaults straight away, so ten files dropped at
/// once are ten jobs on screen. Selecting a job that has not started shows its settings on the
/// setup screen, where every change is saved to that job - and only that job - as it is made.
/// Selecting a started job shows its progress, as the job screen always has.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The latest snapshot of every job, as the engine published it.</summary>
    private readonly Dictionary<string, JobSnapshot> _snapshots = new();

    /// <summary>Documents inspected this session, so a job's setup screen can show everything that was read.</summary>
    private readonly Dictionary<string, PdfDocumentInfo> _documents = new();

    /// <summary>Jobs removed from the list this session.</summary>
    private readonly HashSet<string> _removed = new();

    /// <summary>Page colours by document SHA-256, so a document is never analysed twice.</summary>
    private readonly Dictionary<string, string> _knownColourMaps = new(StringComparer.OrdinalIgnoreCase);

    private readonly PdfIntake _intake;
    private readonly DispatcherTimer _applyTimer;

    public ObservableCollection<QueueItemViewModel> Queue { get; } = new();
    public ObservableCollection<IntakeNotice> IntakeNotices { get; } = new();

    public RelayCommand StartQueueCommand { get; private set; } = null!;
    public RelayCommand MoveUpCommand { get; private set; } = null!;
    public RelayCommand MoveDownCommand { get; private set; } = null!;
    public RelayCommand RemoveJobCommand { get; private set; } = null!;
    public RelayCommand HoldJobCommand { get; private set; } = null!;
    public RelayCommand ClearFinishedCommand { get; private set; } = null!;
    public RelayCommand DismissNoticesCommand { get; private set; } = null!;
    public RelayCommand UseAsDefaultsCommand { get; private set; } = null!;
    public RelayCommand CopyToOthersCommand { get; private set; } = null!;

    private void InitialiseQueueCommands()
    {
        StartQueueCommand = new RelayCommand(StartQueue, () => ReadyCount > 0 && !IsAdding);
        MoveUpCommand = new RelayCommand(p => { if (Item(p) is { CanMoveUp: true } item) _engine.MoveJob(item.Id, -1); });
        MoveDownCommand = new RelayCommand(p => { if (Item(p) is { CanMoveDown: true } item) _engine.MoveJob(item.Id, +1); });
        RemoveJobCommand = new RelayCommand(p => RemoveJob(Item(p) ?? SelectedItem));
        HoldJobCommand = new RelayCommand(() => { if (_job is { State: JobState.Queued, IsWaiting: true } job) _engine.HoldJob(job.Id); });
        ClearFinishedCommand = new RelayCommand(ClearFinished, () => Queue.Any(i => i.Snapshot.IsTerminal));
        DismissNoticesCommand = new RelayCommand(() => { IntakeNotices.Clear(); Raise(nameof(HasNotices)); });
        UseAsDefaultsCommand = new RelayCommand(UseAsDefaults, () => ShowSetup && EditorValid);
        CopyToOthersCommand = new RelayCommand(CopyToOthers, () => ShowSetup && EditorValid && OtherWaitingCount > 0 && !IsAdding);
    }

    private QueueItemViewModel? Item(object? parameter) => parameter as QueueItemViewModel;

    // ---- Selection ------------------------------------------------------------------------

    private bool _syncingQueue;
    private QueueItemViewModel? _selectedItem;

    /// <summary>The job on the right-hand side: its settings if it has not started, its progress if it has.</summary>
    public QueueItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            // The list briefly reports "nothing selected" while its rows are being reordered.
            if (_syncingQueue || value is null && Queue.Count > 0) { Raise(); return; }
            Select(value?.Id);
        }
    }

    private void Select(string? jobId)
    {
        if (jobId == _job?.Id && (jobId is null || _selectedItem?.Id == jobId)) return;

        // Whatever was typed for the previous job belongs to that job.
        FlushEdit();

        _job = jobId is not null && _snapshots.TryGetValue(jobId, out var snapshot) ? snapshot : null;
        _selectedItem = Queue.FirstOrDefault(i => i.Id == jobId);
        Raise(nameof(SelectedItem));

        if (_job is { IsWaiting: true } waiting) LoadEditor(waiting);

        RefreshAll();
        RefreshEvents();
    }

    // ---- The list -------------------------------------------------------------------------

    /// <summary>
    /// Brings the list in line with the snapshots: rows added, removed, moved and refreshed in
    /// place, in queue order.
    /// </summary>
    private void SyncQueue()
    {
        _syncingQueue = true;
        try
        {
            var ordered = _snapshots.Values.OrderBy(j => j.QueuePosition).ThenBy(j => j.CreatedUtc).ToList();
            var ids = ordered.Select(j => j.Id).ToHashSet();

            for (var i = Queue.Count - 1; i >= 0; i--)
                if (!ids.Contains(Queue[i].Id)) Queue.RemoveAt(i);

            var waiting = ordered.Where(j => j.IsWaiting).Select(j => j.Id).ToList();

            // A row names its printer only when the jobs do not all share one.
            var mixedPrinters = ordered.Select(j => j.PrinterName).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();

            for (var i = 0; i < ordered.Count; i++)
            {
                var job = ordered[i];
                var existing = Queue.FirstOrDefault(q => q.Id == job.Id);
                if (existing is null)
                {
                    existing = new QueueItemViewModel(job);
                    Queue.Insert(i, existing);
                }
                else if (Queue.IndexOf(existing) != i)
                {
                    Queue.Move(Queue.IndexOf(existing), i);
                }

                var place = waiting.IndexOf(job.Id);
                existing.Update(job, i + 1, ProblemFor(job),
                    canMoveUp: place > 0, canMoveDown: place >= 0 && place < waiting.Count - 1,
                    showPrinter: mixedPrinters);
            }
        }
        finally
        {
            _syncingQueue = false;
        }

        // Keep something on screen: the selected job, else the one printing, else the first.
        if (_job is null || !_snapshots.ContainsKey(_job.Id))
        {
            var fallback = Queue.FirstOrDefault(i => i.IsStarted && !i.Snapshot.IsTerminal) ?? Queue.FirstOrDefault();
            _job = null;
            Select(fallback?.Id);
        }
        else
        {
            _selectedItem = Queue.FirstOrDefault(i => i.Id == _job.Id);
            Raise(nameof(SelectedItem));
        }

        foreach (var name in new[]
                 {
                     nameof(QueueSummary), nameof(QueueCounts), nameof(QueueOverview), nameof(HasJobs),
                     nameof(ShowEmptyState), nameof(ReadyCount), nameof(StartQueueLabel), nameof(OtherWaitingCount),
                     nameof(QueueHeadline), nameof(HasActiveWork), nameof(HasFinishedJobs),
                 })
        {
            Raise(name);
        }

        StartQueueCommand.RaiseCanExecuteChanged();
        ClearFinishedCommand.RaiseCanExecuteChanged();
        CopyToOthersCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Something that will stop a job that has not started from printing, found before it gets
    /// its turn: its file gone, its printer gone, or its printer refusing its settings.
    /// </summary>
    private string? ProblemFor(JobSnapshot job)
    {
        if (!job.IsWaiting) return null;

        if (!File.Exists(job.SourcePath)) return $"The PDF is no longer at {job.SourcePath}.";
        if (Printers.Count > 0 && !_spooler.GetPrinter(job.PrinterName).Exists)
            return $"The printer \"{job.PrinterName}\" is not installed.";

        var settings = job.PrintSettings;
        if (settings.PrinterSetup == PrinterSetup.Default) return null;

        var margin = settings.IsImposed ? settings.MarginMm : (double?)null;
        return SetupReportFor(job.PrinterName, settings.PrinterSetup, margin, out _)?.FirstBlocking is { } blocking
            ? $"{blocking.Setting}: {blocking.Message}"
            : null;
    }

    public bool HasJobs => Queue.Count > 0;
    public bool ShowEmptyState => Queue.Count == 0;

    /// <summary>"5 jobs · 486 pages"</summary>
    public string QueueSummary
    {
        get
        {
            if (_snapshots.Count == 0) return "No jobs yet";
            var pages = _snapshots.Values.Sum(j => j.SelectedPageCount);
            return $"{_snapshots.Count} job{(_snapshots.Count == 1 ? "" : "s")}  ·  {pages} pages";
        }
    }

    /// <summary>"1 printing · 3 waiting · 1 finished": only what the application actually knows.</summary>
    public string QueueCounts
    {
        get
        {
            var jobs = _snapshots.Values.ToList();
            var parts = new List<string>();
            void Add(int count, string what) { if (count > 0) parts.Add($"{count} {what}"); }

            Add(jobs.Count(j => j.State is JobState.PreparingBatch or JobState.PrintingBatch or JobState.VerifyingBatch
                                    or JobState.CoolingDown or JobState.WaitingForStart), "printing");
            Add(jobs.Count(j => j.State == JobState.Queued), "waiting");
            Add(jobs.Count(j => j.State == JobState.Draft), "not started");
            Add(jobs.Count(j => j.State is JobState.NeedsAttention or JobState.BlockedPrinterError
                                    or JobState.BlockedPrinterOffline or JobState.Failed), "need you");
            Add(jobs.Count(j => j.State == JobState.Paused), "paused");
            Add(jobs.Count(j => j.State == JobState.Completed), "finished");
            Add(jobs.Count(j => j.State == JobState.Cancelled), "cancelled");
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>"5 jobs · 486 pages · 1 printing · 4 not started": the queue in one line, under its heading.</summary>
    public string QueueOverview => string.IsNullOrEmpty(QueueCounts) ? QueueSummary : $"{QueueSummary}  ·  {QueueCounts}";

    /// <summary>Whether there is anything for "Clear finished jobs" to clear, so the link only shows when it can act.</summary>
    public bool HasFinishedJobs => _snapshots.Values.Any(j => j.IsTerminal);

    /// <summary>For the notification-area tooltip: what the queue is doing right now.</summary>
    public string QueueHeadline
    {
        get
        {
            var running = _snapshots.Values
                .Where(j => j.IsActive && j.State is not (JobState.Queued or JobState.NeedsAttention))
                .OrderBy(j => j.QueuePosition)
                .FirstOrDefault();
            var waiting = _snapshots.Values.Count(j => j.State == JobState.Queued);
            var needing = _snapshots.Values.Count(j => j.State == JobState.NeedsAttention);

            var headline = running is null
                ? waiting > 0 ? $"{waiting} job(s) waiting" : "Idle"
                : $"{QueueItemViewModel.Describe(running)} {running.Label}, {running.FileName}";
            if (running is not null && waiting > 0) headline += $"; {waiting} waiting";
            if (needing > 0) headline += $"; {needing} need(s) you";
            return headline;
        }
    }

    /// <summary>Whether anything is started and unfinished, so closing PrintPacer would stop printing.</summary>
    public bool HasActiveWork => _snapshots.Values.Any(j => j.IsActive);

    /// <summary>Jobs not yet started that can be: a job with a problem already known stays out until it is fixed.</summary>
    public int ReadyCount => Queue.Count(i => i.Snapshot.State == JobState.Draft && i.Problem is null);
    public int OtherWaitingCount => _snapshots.Values.Count(j => j.IsWaiting && j.Id != _job?.Id);

    public string StartQueueLabel
    {
        get
        {
            var ready = ReadyCount;
            var busy = _snapshots.Values.Any(j => j.IsActive);
            return ready switch
            {
                0 => busy ? "Queue running" : "Start printing",
                1 when !busy => "Start printing",
                _ when !busy => $"Start printing  ·  {ready} jobs",
                _ => $"Add {ready} job{(ready == 1 ? "" : "s")} to the queue",
            };
        }
    }

    // ---- Adding files ---------------------------------------------------------------------

    private bool _isAdding;
    public bool IsAdding
    {
        get => _isAdding;
        private set
        {
            if (!Set(ref _isAdding, value)) return;
            StartQueueCommand.RaiseCanExecuteChanged();
            CopyToOthersCommand.RaiseCanExecuteChanged();
        }
    }

    private string? _addingStatus;
    public string? AddingStatus
    {
        get => _addingStatus;
        private set => Set(ref _addingStatus, value);
    }

    public bool HasNotices => IntakeNotices.Count > 0;

    /// <summary>
    /// Adds dropped or chosen files, one job per valid PDF, each configured from the defaults.
    /// Files that cannot be printed are listed with the reason; they never stop the others.
    /// </summary>
    public async void AddFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (IsAdding)
        {
            _pendingFiles.AddRange(paths);
            return;
        }

        IsAdding = true;
        IntakeNotices.Clear();
        string? first = null;
        var added = 0;

        try
        {
            var progress = new Progress<(int Index, int Count, string FileName)>(p =>
                AddingStatus = $"Reading {p.FileName}  ({p.Index + 1} of {p.Count})…");
            var result = await Task.Run(() => _intake.Load(paths, progress));

            foreach (var rejected in result.Rejected)
                IntakeNotices.Add(new IntakeNotice(rejected.FileName, rejected.Reason, IsError: true));

            var printer = DefaultPrinter();
            foreach (var document in result.Accepted)
            {
                if (printer is null)
                {
                    IntakeNotices.Add(new IntakeNotice(document.FileName, "Not added: no printer is installed in Windows.", true));
                    continue;
                }

                if (_snapshots.Values.FirstOrDefault(j => j.IsWaiting &&
                        string.Equals(j.FileSha256, document.Sha256, StringComparison.OrdinalIgnoreCase)) is { } twin)
                {
                    IntakeNotices.Add(new IntakeNotice(document.FileName,
                        $"Already waiting as {twin.Label}; added again as a separate job. Remove one if that was a mistake.", false));
                }

                var configuration = JobConfiguration.FromDefaults(Settings, printer,
                    _knownColourMaps.GetValueOrDefault(document.Sha256));

                if (configuration.NeedsPageColours(document.PageCount))
                {
                    AddingStatus = $"Checking which pages of {document.FileName} use colour, for the invoice…";
                    try
                    {
                        var map = await Task.Run(() => PageColourMap.Encode(PageColourAnalyzer.Analyse(document.Path)));
                        _knownColourMaps[document.Sha256] = map;
                        configuration = configuration with { Invoice = configuration.Invoice! with { PageColourMap = map } };
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Colour check failed for {File}", document.FileName);
                        configuration = configuration with { Invoice = null };
                        IntakeNotices.Add(new IntakeNotice(document.FileName,
                            "Added without an invoice: its pages could not be checked for colour.", false));
                    }
                }

                AddingStatus = $"Adding {document.FileName}…";
                try
                {
                    var job = await _engine.CreateJobAsync(document, configuration);
                    _documents[job.Id] = document;
                    _snapshots[job.Id] = job;
                    first ??= job.Id;
                    added++;
                }
                catch (Exception ex)
                {
                    IntakeNotices.Add(new IntakeNotice(document.FileName, $"Not added: {Plain(ex.Message)}", true));
                }
            }

            Log.Information("Added {Added} job(s) from {Files} file(s); {Rejected} refused",
                added, paths.Count, result.Rejected.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Adding files failed");
            IntakeNotices.Add(new IntakeNotice("", $"The files could not be added: {ex.Message}", true));
        }
        finally
        {
            AddingStatus = null;
            IsAdding = false;
            Raise(nameof(HasNotices));
        }

        SyncQueue();
        if (first is not null) Select(first);

        if (_pendingFiles.Count > 0)
        {
            var more = _pendingFiles.ToList();
            _pendingFiles.Clear();
            AddFiles(more);
        }
    }

    private readonly List<string> _pendingFiles = new();

    /// <summary>The printer new jobs get: the default from Settings, else the Windows default, else the first.</summary>
    private string? DefaultPrinter()
    {
        var installed = _spooler.ListPrinterNames();
        return Settings.LastPrinterName is { } chosen && installed.Contains(chosen) ? chosen
            : _spooler.GetDefaultPrinterName() is { } windows && installed.Contains(windows) ? windows
            : installed.FirstOrDefault();
    }

    private static string Plain(string message) => message.Split(" (Parameter")[0];

    // ---- Starting --------------------------------------------------------------------------

    /// <summary>
    /// Puts every job that has not started into the queue, in list order. A job with a problem
    /// already known - its file gone, its printer refusing its settings - stays behind, and the
    /// user is told why.
    /// </summary>
    private void StartQueue()
    {
        FlushEdit();

        var drafts = Queue.Where(i => i.Snapshot.State == JobState.Draft).ToList();
        if (drafts.Count == 0) return;
        var blocked = drafts.Where(i => i.Problem is not null).ToList();
        var ready = drafts.Except(blocked).Select(i => i.Id).ToList();

        IntakeNotices.Clear();
        foreach (var item in blocked)
            IntakeNotices.Add(new IntakeNotice(item.FileName, $"Not started: {item.Problem}", true));
        Raise(nameof(HasNotices));

        if (ready.Count == 0) return;
        _engine.StartQueue(ready);
    }

    // ---- Removing -------------------------------------------------------------------------

    private async void RemoveJob(QueueItemViewModel? item)
    {
        if (item is null) return;
        var job = item.Snapshot;

        if (!item.CanRemove)
        {
            MessageBox.Show($"{job.Label} ({job.FileName}) has started. Cancel it first; it can then be removed from the list.",
                "Remove job", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var question = job.IsWaiting
            ? $"Remove {job.FileName} from the queue?\n\nNothing of it has been printed."
            : $"Remove {job.FileName} from the list?\n\nWhat happened to it stays in the activity log.";
        if (MessageBox.Show(question, "Remove job", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;

        await Remove(job);
    }

    private async Task Remove(JobSnapshot job)
    {
        if (_pendingApplyJobId == job.Id) CancelPendingEdit();

        var refusal = await _engine.RemoveJobAsync(job.Id);
        if (refusal is not null)
        {
            MessageBox.Show($"{job.Label} was not removed. {refusal}", "Remove job",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var index = Queue.ToList().FindIndex(i => i.Id == job.Id);
        _removed.Add(job.Id);
        _snapshots.Remove(job.Id);
        _documents.Remove(job.Id);

        if (_job?.Id == job.Id)
        {
            _job = null;
            var neighbour = Queue.Where(i => i.Id != job.Id).ElementAtOrDefault(Math.Max(0, index - 1));
            SyncQueue();
            Select(neighbour?.Id ?? Queue.FirstOrDefault()?.Id);
        }
        else
        {
            SyncQueue();
        }
    }

    private async void ClearFinished()
    {
        var finished = Queue.Where(i => i.Snapshot.IsTerminal).Select(i => i.Snapshot).ToList();
        if (finished.Count == 0) return;

        if (MessageBox.Show($"Remove {finished.Count} finished or cancelled job(s) from the list?\n\n" +
                            "What happened to them stays in the activity log.", "Clear finished jobs",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        foreach (var job in finished) await Remove(job);
    }

    // ---- Editing one job ---------------------------------------------------------------------

    private bool _loadingEditor;
    private string? _pendingApplyJobId;
    private JobConfiguration? _pendingApply;

    private string? _saveStatus;

    /// <summary>"Saved", or why the last change is not saved yet.</summary>
    public string? SaveStatus
    {
        get => _saveStatus;
        private set
        {
            if (Set(ref _saveStatus, value)) Raise(nameof(SaveNote));
        }
    }

    /// <summary>
    /// The line beside "Copy to other jobs" and "Use as defaults": whether the last change is
    /// saved, and until there is one, whose settings these are.
    /// </summary>
    public string SaveNote => SaveStatus ?? "These settings belong to this job only, and are saved as you change them.";

    /// <summary>"Job #3 · Not started"</summary>
    public string EditorJobLabel => _job is null ? "" : $"{_job.Label}  ·  {QueueItemViewModel.Describe(_job)}";

    /// <summary>Where a job waiting in the queue stands. A job not started has nothing more to say than its label does.</summary>
    public string EditorStatus
    {
        get
        {
            if (_job is null || _job.State == JobState.Draft) return "";

            // The engine's note, when it has worked out a start time, already says where the job
            // stands ("Next in the queue. Starts at 09:59, after…"), so it replaces the count.
            var ahead = _snapshots.Values.Count(j => j.Id != _job.Id && j.IsActive &&
                                                     (j.State != JobState.Queued || j.QueuePosition < _job.QueuePosition));
            var where = _job.LastPrinterStatus is { Length: > 0 } note && note.Contains("Starts at")
                ? note
                : ahead == 0 ? "Next in the queue." : $"Waiting in the queue behind {ahead} job(s).";
            return where + " You can still change it until it starts.";
        }
    }

    public bool CanHold => _job is { State: JobState.Queued, IsWaiting: true };

    /// <summary>Shows a job's stored settings on the setup screen, without saving anything back.</summary>
    private void LoadEditor(JobSnapshot job)
    {
        _loadingEditor = true;
        try
        {
            var c = job.Configuration;

            if (!Printers.Contains(c.PrinterName)) Printers.Add(c.PrinterName);
            SelectedPrinter = c.PrinterName;

            PagesPerBatch = c.PagesPerBatch.ToString(CultureInfo.CurrentCulture);
            DelayMinutes = c.DelayMinutes.ToString(CultureInfo.CurrentCulture);

            StartLater = c is { StartMode: StartMode.AtTime, ScheduledStartUtc: not null };
            if (c.ScheduledStartUtc is { } at && StartLater)
            {
                var local = at.ToLocalTime();
                StartDate = local.Date;
                StartTime = local.ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            else
            {
                StartDate = DateTime.Today;
                StartTime = DateTime.Now.AddMinutes(30).ToString("HH:mm", CultureInfo.InvariantCulture);
            }

            RenderDpi = c.RenderDpi;
            ColourMode = c.ColourMode;

            var print = c.PrintSettings;
            _layout = print.Layout;
            _doubleSided = print.Duplex != DuplexMode.SingleSided;
            _flip = print.Duplex == DuplexMode.SingleSided ? DuplexMode.LongEdge : print.Duplex;
            _normalDoubleSided = false;
            _normalFlip = DuplexMode.LongEdge;
            PageSelection = print.PageSelection;
            Paper = print.Paper;
            MarginMm = FormatMm(print.MarginMm);
            FoldGapMm = FormatMm(print.FoldGapMm);
            Scaling = print.Scaling;
            Binding = print.Binding;
            SheetsPerSection = print.SheetsPerSignature;
            KeepBackCover = print.KeepLastPageAsBackCover;
            Fold = print.Fold;

            if (c.Invoice is { } invoice)
            {
                if (PageColourMap.IsValid(invoice.PageColourMap, job.TotalPages))
                    _knownColourMaps[job.FileSha256] = invoice.PageColourMap!;
                BlackWhitePrice = FormatPrice(invoice.BlackWhitePagePrice);
                ColourPrice = FormatPrice(invoice.ColourPagePrice);
            }
            else
            {
                BlackWhitePrice = FormatPrice(Settings.BlackWhitePagePrice);
                ColourPrice = FormatPrice(Settings.ColourPagePrice);
            }
            PrintInvoice = c.Invoice is not null;

            PreviewStatus = null;
            Document = DocumentFor(job);
        }
        finally
        {
            _loadingEditor = false;
        }

        RaiseLayoutProperties();
        RefreshColourModeNote();
        RefreshPrintSettings();
        SaveStatus = null;
    }

    /// <summary>What is known about a job's document: all of it if it was added this session.</summary>
    private PdfDocumentInfo DocumentFor(JobSnapshot job) =>
        _documents.TryGetValue(job.Id, out var document) && document.Sha256 == job.FileSha256
            ? document
            : new PdfDocumentInfo
            {
                Path = job.SourcePath,
                FileName = job.FileName,
                FileSizeBytes = job.Document.FileSizeBytes,
                Sha256 = job.FileSha256,
                PageCount = job.TotalPages,
                Title = job.Document.Title,
                Author = job.Document.Author,
                Producer = job.Document.Producer,
                PdfVersion = job.Document.PdfVersion,
                IsEncrypted = job.Document.IsEncrypted,
            };

    /// <summary>
    /// The setup screen's choices as a configuration for the selected job, or null (with the
    /// reason in <see cref="SetupProblem"/>) while something cannot be read yet.
    /// </summary>
    private JobConfiguration? BuildEditorConfiguration()
    {
        if (!EditorValid || SelectedPrinter is null) return null;
        if (BuildPlan(out _) is not { } plan) return null;
        if (!TryReadPagesPerBatch(out var perBatch, out _) || !TryReadDelay(out var delay, out _)) return null;

        var start = ResolveStart(out var startProblem);
        if (startProblem is not null) return null;

        InvoiceRequest? invoice = null;
        if (PrintInvoice)
        {
            if (!TryReadPrices(out var blackWhite, out var colour, out _)) return null;
            if (!BlackAndWhite && _colourMap is null) return null;
            invoice = new InvoiceRequest(blackWhite, colour, BlackAndWhite ? null : _colourMap);
        }

        return new JobConfiguration
        {
            PrinterName = SelectedPrinter,
            PagesPerBatch = perBatch,
            DelayMinutes = delay,
            StartMode = StartLater ? StartMode.AtTime : StartMode.Immediately,
            ScheduledStartUtc = start?.ToUniversalTime(),
            RenderDpi = RenderDpi,
            ColourMode = ColourMode,
            PrintSettings = plan.Settings,
            Invoice = invoice,
        };
    }

    /// <summary>Called whenever the setup screen changes: saves it to the job shortly after the typing stops.</summary>
    private void EditorChanged()
    {
        if (_loadingEditor || _job is not { IsWaiting: true } job) return;

        if (BuildEditorConfiguration() is not { } configuration)
        {
            SaveStatus = SetupProblem is null ? null : "Not saved yet: " + SetupProblem.Split('\n')[0];
            return;
        }

        if (configuration == job.Configuration && _pendingApplyJobId is null)
        {
            SaveStatus = "All changes saved.";
            return;
        }

        _pendingApplyJobId = job.Id;
        _pendingApply = configuration;
        SaveStatus = "Saving…";
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    private void CancelPendingEdit()
    {
        _applyTimer.Stop();
        _pendingApplyJobId = null;
        _pendingApply = null;
    }

    /// <summary>Saves a pending change now - before another job is selected, or the queue started.</summary>
    private void FlushEdit()
    {
        if (_pendingApplyJobId is null) return;
        _applyTimer.Stop();
        _ = ApplyPendingEditAsync();
    }

    private async Task ApplyPendingEditAsync()
    {
        _applyTimer.Stop();
        if (_pendingApplyJobId is not { } jobId || _pendingApply is not { } configuration) return;
        CancelPendingEdit();

        try
        {
            var updated = await _engine.UpdateJobAsync(jobId, configuration);
            _snapshots[jobId] = updated;
            if (_job?.Id == jobId)
            {
                _job = updated;
                SaveStatus = "All changes saved.";
            }
            SyncQueue();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save the settings of job {Job}", jobId);
            if (_job?.Id == jobId) SaveStatus = "Not saved: " + Plain(ex.Message);
        }
    }

    // ---- Defaults and copying ----------------------------------------------------------------

    /// <summary>
    /// Makes the selected job's settings the defaults for files added from now on. The only way,
    /// besides the Settings window, that the defaults ever change.
    /// </summary>
    private void UseAsDefaults()
    {
        if (BuildEditorConfiguration() is not { } c) return;

        Settings.LastPrinterName = c.PrinterName;
        Settings.DefaultPagesPerBatch = TypedBatchSize(c);
        Settings.DefaultDelayMinutes = c.DelayMinutes;
        Settings.ColourMode = c.ColourMode;
        Settings.RenderDpi = c.RenderDpi;
        if (!c.PrintSettings.IsImposed) Settings.DefaultDuplex = c.PrintSettings.Duplex;
        else Settings.LayoutDefaults = c.PrintSettings;
        Settings.PrintInvoice = c.Invoice is not null;
        if (c.Invoice is { } invoice)
        {
            Settings.BlackWhitePagePrice = invoice.BlackWhitePagePrice;
            Settings.ColourPagePrice = invoice.ColourPagePrice;
        }
        ApplySettings(Settings);

        SaveStatus = "These settings are now the defaults for PDFs you add. Jobs already in the list keep their own.";
    }

    /// <summary>
    /// The batch size as typed, not as this document clamps it: 25 per batch is still 25 for the
    /// next, longer document, even when this one only has 18 pages.
    /// </summary>
    private int TypedBatchSize(JobConfiguration fallback) =>
        int.TryParse(PagesPerBatch, NumberStyles.Integer, CultureInfo.CurrentCulture, out var typed) && typed > 0
            ? typed
            : fallback.PagesPerBatch;

    /// <summary>
    /// Copies the selected job's settings to every other job that has not started. Each job gets
    /// its own copy - with its own page colours for the invoice - so they can still be changed
    /// one by one afterwards. A job the settings do not suit (an even-page selection of a
    /// one-page PDF) is left as it was, and listed.
    /// </summary>
    private async void CopyToOthers()
    {
        FlushEdit();
        if (BuildEditorConfiguration() is not { } source || _job is not { } from) return;

        var targets = _snapshots.Values.Where(j => j.IsWaiting && j.Id != from.Id)
            .OrderBy(j => j.QueuePosition).ToList();
        if (targets.Count == 0) return;

        if (MessageBox.Show($"Copy the settings of {from.FileName} to the {targets.Count} other job(s) that have not started?\n\n" +
                            "Each keeps its own document, and can still be changed on its own afterwards.",
                "Copy settings", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        IsAdding = true;
        IntakeNotices.Clear();
        var copied = 0;
        try
        {
            foreach (var target in targets)
            {
                var configuration = source with
                {
                    PagesPerBatch = TypedBatchSize(source),
                    Invoice = source.Invoice is { } invoice
                        ? invoice with { PageColourMap = _knownColourMaps.GetValueOrDefault(target.FileSha256) }
                        : null,
                };

                if (configuration.NeedsPageColours(target.TotalPages))
                {
                    AddingStatus = $"Checking which pages of {target.FileName} use colour…";
                    try
                    {
                        var map = await Task.Run(() => PageColourMap.Encode(PageColourAnalyzer.Analyse(target.SourcePath)));
                        _knownColourMaps[target.FileSha256] = map;
                        configuration = configuration with { Invoice = configuration.Invoice! with { PageColourMap = map } };
                    }
                    catch (Exception ex)
                    {
                        IntakeNotices.Add(new IntakeNotice(target.FileName, $"Not changed: its pages could not be checked for colour ({ex.Message}).", true));
                        continue;
                    }
                }

                try
                {
                    _snapshots[target.Id] = await _engine.UpdateJobAsync(target.Id, configuration);
                    copied++;
                }
                catch (Exception ex)
                {
                    IntakeNotices.Add(new IntakeNotice(target.FileName, $"Not changed: {Plain(ex.Message)}", true));
                }
            }
        }
        finally
        {
            AddingStatus = null;
            IsAdding = false;
            Raise(nameof(HasNotices));
        }

        SyncQueue();
        SaveStatus = $"Settings copied to {copied} of {targets.Count} other job(s).";
    }
}
