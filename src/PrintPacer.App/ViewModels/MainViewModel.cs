using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PrintPacer.Core.Billing;
using PrintPacer.Core.Device;
using PrintPacer.Core.Diagnostics;
using PrintPacer.Core.Engine;
using PrintPacer.Core.Model;
using PrintPacer.Core.Pdf;
using PrintPacer.Core.Persistence;
using PrintPacer.Core.Printing;
using PrintPacer.Core.Spooler;
using Serilog;

namespace PrintPacer.App.ViewModels;

/// <summary>
/// The whole user interface, in one view model.
///
/// It deliberately owns no job state of its own: everything shown is projected from the
/// engine's persisted job whenever the engine says it changed. That
/// keeps the screen and the database incapable of disagreeing, which matters for an
/// application whose main job is to be believed about what has and has not printed.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MainViewModel>();

    private readonly JobEngine _engine;
    private readonly ISpooler _spooler;
    private readonly PdfService _pdf;
    private readonly SettingsStore _settingsStore;
    private readonly DeviceProbeFactory _probes;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _ticker;

    private JobSnapshot? _job;
    private DateTimeOffset _lastDeviceProbe = DateTimeOffset.MinValue;

    public MainViewModel(JobEngine engine, ISpooler spooler, PdfService pdf,
        SettingsStore settingsStore, DeviceProbeFactory probes, AppSettings settings)
    {
        _engine = engine;
        _spooler = spooler;
        _pdf = pdf;
        _settingsStore = settingsStore;
        _probes = probes;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Settings = settings;

        _pagesPerBatch = settings.DefaultPagesPerBatch.ToString();
        _delayMinutes = settings.DefaultDelayMinutes.ToString();
        _colourMode = settings.ColourMode;
        _printInvoice = settings.PrintInvoice;
        _blackWhitePrice = FormatPrice(settings.BlackWhitePagePrice);
        _colourPrice = FormatPrice(settings.ColourPagePrice);

        BrowseCommand = new RelayCommand(Browse);
        StartCommand = new RelayCommand(Start, () => CanStart);
        ClearDocumentCommand = new RelayCommand(() => Document = null);
        PauseCommand = new RelayCommand(() => WithJob(j => _engine.PauseJob(j.Id)));
        ResumeCommand = new RelayCommand(() => WithJob(j => _engine.ResumeJob(j.Id)));
        CancelCommand = new RelayCommand(CancelJob);
        NewJobCommand = new RelayCommand(NewJob);
        OpenQueueCommand = new RelayCommand(OpenPrintQueue);
        OpenPrinterSettingsCommand = new RelayCommand(OpenPrinterSettings);
        OpenLogFolderCommand = new RelayCommand(() => OpenPath(AppPaths.LogDirectory));
        ExportLogCommand = new RelayCommand(ExportLog);
        RefreshPrintersCommand = new RelayCommand(RefreshPrinters);
        CancelQueuedBatchCommand = new RelayCommand(() => WithJob(j => _engine.CancelCurrentSpoolerJob(j.Id)));
        PrintInvoiceCommand = new RelayCommand(PrintInvoiceNow);

        ConfirmPrintedCommand = new RelayCommand(p => WithJob(j =>
        {
            if (p is BatchViewModel b) _engine.ConfirmBatchPrinted(j.Id, b.Number);
        }));

        ConfirmNotPrintedCommand = new RelayCommand(p => WithJob(j =>
        {
            if (p is BatchViewModel b && ConfirmResend(b)) _engine.ConfirmBatchNotPrinted(j.Id, b.Number);
        }));

        ResumeFromCommand = new RelayCommand(p => WithJob(j =>
        {
            if (p is BatchViewModel b) _engine.ResumeBatchFrom(j.Id, b.Number, b.ConfirmedPages);
        }));

        RetryBatchCommand = new RelayCommand(p => WithJob(j =>
        {
            if (p is BatchViewModel b && ConfirmResend(b)) _engine.RetryBatch(j.Id, b.Number);
        }));

        _engine.JobChanged += job => _dispatcher.BeginInvoke(() => OnJobChanged(job));
        _engine.EventAppended += _ => _dispatcher.BeginInvoke(RefreshEvents);

        RefreshPrinters();

        // One second is the right cadence: it keeps the countdown honest without the
        // application doing meaningful work while it sits waiting overnight.
        _ticker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => Tick();
        _ticker.Start();

        AdoptExistingJob();
    }

    public AppSettings Settings { get; private set; }

    // ---- Commands -----------------------------------------------------------

    public RelayCommand BrowseCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand ClearDocumentCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand NewJobCommand { get; }
    public RelayCommand OpenQueueCommand { get; }
    public RelayCommand OpenPrinterSettingsCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand ExportLogCommand { get; }
    public RelayCommand RefreshPrintersCommand { get; }
    public RelayCommand ConfirmPrintedCommand { get; }
    public RelayCommand ConfirmNotPrintedCommand { get; }
    public RelayCommand ResumeFromCommand { get; }
    public RelayCommand RetryBatchCommand { get; }
    public RelayCommand CancelQueuedBatchCommand { get; }
    public RelayCommand PrintInvoiceCommand { get; }

    // ---- Setup screen --------------------------------------------------------

    public ObservableCollection<string> Printers { get; } = new();

    private string? _selectedPrinter;
    public string? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (!Set(ref _selectedPrinter, value)) return;
            Raise(nameof(PrinterHint));
            RefreshColourModeNote();
            RefreshCanStart();
            if (value is not null)
            {
                Settings.LastPrinterName = value;
                _settingsStore.Save(Settings);
                _lastDeviceProbe = DateTimeOffset.MinValue;
            }
        }
    }

    private PdfDocumentInfo? _document;
    public PdfDocumentInfo? Document
    {
        get => _document;
        private set
        {
            if (!Set(ref _document, value)) return;
            foreach (var name in new[]
                     {
                         nameof(HasDocument), nameof(DocumentName), nameof(DocumentPages),
                         nameof(DocumentDetail), nameof(DocumentWarning), nameof(BatchPreview),
                     })
            {
                Raise(name);
            }
            RestartColourCheck();
            RefreshCanStart();
        }
    }

    public bool HasDocument => Document is not null;
    public string DocumentName => Document?.FileName ?? "";
    public string DocumentPages => Document is null ? "" : $"{Document.PageCount} pages";

    public string DocumentDetail
    {
        get
        {
            if (Document is null) return "";
            var parts = new List<string> { Document.SizeDisplay };
            if (Document.PageSizeDisplay is { } size) parts.Add(size);
            if (Document.PdfVersion is { } version) parts.Add($"PDF {version}");
            if (!string.IsNullOrWhiteSpace(Document.Title)) parts.Add($"“{Document.Title}”");
            if (!string.IsNullOrWhiteSpace(Document.Author)) parts.Add(Document.Author!);
            return string.Join("  ·  ", parts);
        }
    }

    public string? DocumentWarning => Document?.Warnings.Count > 0
        ? string.Join(" ", Document.Warnings)
        : null;

    private string _pagesPerBatch;
    public string PagesPerBatch
    {
        get => _pagesPerBatch;
        set { if (Set(ref _pagesPerBatch, value)) { Raise(nameof(BatchPreview)); RefreshCanStart(); } }
    }

    private string _delayMinutes;
    public string DelayMinutes
    {
        get => _delayMinutes;
        set { if (Set(ref _delayMinutes, value)) { Raise(nameof(BatchPreview)); RefreshCanStart(); } }
    }

    private bool _startLater;
    public bool StartLater
    {
        get => _startLater;
        set { if (Set(ref _startLater, value)) { Raise(nameof(BatchPreview)); RefreshCanStart(); } }
    }

    private DateTime _startDate = DateTime.Today;
    public DateTime StartDate
    {
        get => _startDate;
        set { if (Set(ref _startDate, value)) { Raise(nameof(BatchPreview)); RefreshCanStart(); } }
    }

    private string _startTime = DateTime.Now.AddMinutes(30).ToString("HH:mm");
    public string StartTime
    {
        get => _startTime;
        set { if (Set(ref _startTime, value)) { Raise(nameof(BatchPreview)); RefreshCanStart(); } }
    }

    // ---- Print mode ------------------------------------------------------------------

    public sealed record ColourModeOption(ColourMode Mode, string Label)
    {
        // What screen readers announce for the item.
        public override string ToString() => Label;
    }

    public IReadOnlyList<ColourModeOption> ColourModes { get; } = Enum.GetValues<ColourMode>()
        .Select(mode => new ColourModeOption(mode, mode.Describe()))
        .ToList();

    private ColourMode _colourMode;

    /// <summary>
    /// The mode for the job being set up, and the only place the setup screen keeps it. It
    /// starts at the last job's choice and goes into the job itself when Start is pressed.
    /// </summary>
    public ColourMode ColourMode
    {
        get => _colourMode;
        set
        {
            if (!Set(ref _colourMode, value)) return;
            Raise(nameof(BatchPreview));
            RefreshColourModeNote();

            // Black and white makes every page a black and white page on the bill, so the
            // colour check is only needed when printing in colour.
            RestartColourCheck();
            Raise(nameof(InvoiceEstimate));
            RefreshCanStart();
        }
    }

    private bool BlackAndWhite => ColourMode == ColourMode.BlackAndWhite;

    /// <summary>What each printer's driver said about black and white, asked once per printer.</summary>
    private readonly Dictionary<string, MonochromeCheck> _monochromeChecks = new();
    private readonly HashSet<string> _monochromeChecksRunning = new();

    /// <summary>
    /// Under the print mode: what black and white will actually mean on the chosen printer,
    /// including a warning when its driver cannot be told to use black ink only.
    /// </summary>
    public string? ColourModeNote
    {
        get
        {
            if (!BlackAndWhite || SelectedPrinter is null) return null;
            if (!_monochromeChecks.TryGetValue(SelectedPrinter, out var check)) return "Checking the printer driver…";

            return check.Support switch
            {
                MonochromeSupport.Honoured =>
                    "The printer is asked to use black ink only, and every page is sent in greyscale.",
                MonochromeSupport.MonochromeDevice => "This printer only prints in black & white.",
                _ => check.Warning(SelectedPrinter),
            };
        }
    }

    public Brush ColourModeNoteBrush =>
        BlackAndWhite && SelectedPrinter is not null &&
        _monochromeChecks.TryGetValue(SelectedPrinter, out var check) && !check.Guaranteed
            ? Resource("Warning")
            : Resource("TextSecondary");

    private void RefreshColourModeNote()
    {
        Raise(nameof(ColourModeNote));
        Raise(nameof(ColourModeNoteBrush));

        if (!BlackAndWhite || SelectedPrinter is not { } printer) return;
        if (_monochromeChecks.ContainsKey(printer) || !_monochromeChecksRunning.Add(printer)) return;

        // Asking the driver can take a moment on a network printer, so keep it off the UI
        // thread. The check never throws: a driver that fails comes back as Unknown.
        _ = Task.Run(() =>
        {
            var check = PrinterColour.Check(printer);
            _dispatcher.BeginInvoke(() =>
            {
                _monochromeChecksRunning.Remove(printer);
                _monochromeChecks[printer] = check;
                Raise(nameof(ColourModeNote));
                Raise(nameof(ColourModeNoteBrush));
            });
        });
    }

    /// <summary>
    /// A plain-language preview of the exact schedule, shown before anything is committed.
    /// The user should never have to guess what pressing the button will do.
    /// </summary>
    public string BatchPreview
    {
        get
        {
            if (Document is null) return "Add a PDF to see how it will be split.";
            if (!TryReadPagesPerBatch(out var perBatch, out var problem)) return problem!;
            if (!TryReadDelay(out var delay, out var delayProblem)) return delayProblem!;

            var batches = PrintJob.PlanBatches("preview", Document.PageCount, perBatch);
            var start = ResolveStart(out var startProblem);
            if (startProblem is not null) return startProblem;

            var mode = ColourMode.Describe().ToLowerInvariant();
            var lines = new List<string>
            {
                batches.Count == 1
                    ? $"One print job of {batches[0].PageCount} page(s), in {mode}."
                    : $"{batches.Count} separate print jobs, {delay} minute(s) apart, every one in {mode}.",
            };

            var clock = start ?? DateTimeOffset.Now;
            foreach (var batch in batches.Take(4))
            {
                lines.Add($"    Batch {batch.Number}:  {batch.RangeLabel,-18}  {clock.LocalDateTime:ddd HH:mm}");
                clock = clock.AddMinutes(delay);
            }

            if (batches.Count > 4)
            {
                var last = batches[^1];
                var finish = (start ?? DateTimeOffset.Now).AddMinutes((double)delay * (batches.Count - 1));
                lines.Add($"    …");
                lines.Add($"    Batch {last.Number}:  {last.RangeLabel,-18}  {finish.LocalDateTime:ddd HH:mm}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    // ---- Invoice (setup) --------------------------------------------------------

    private bool _printInvoice;
    public bool PrintInvoice
    {
        get => _printInvoice;
        set
        {
            if (!Set(ref _printInvoice, value)) return;
            RestartColourCheck();
            Raise(nameof(InvoiceEstimate));
            RefreshCanStart();
        }
    }

    private string _blackWhitePrice;
    public string BlackWhitePrice
    {
        get => _blackWhitePrice;
        set { if (Set(ref _blackWhitePrice, value)) { Raise(nameof(InvoiceEstimate)); RefreshCanStart(); } }
    }

    private string _colourPrice;
    public string ColourPrice
    {
        get => _colourPrice;
        set { if (Set(ref _colourPrice, value)) { Raise(nameof(InvoiceEstimate)); RefreshCanStart(); } }
    }

    public string CurrencySymbol => CultureInfo.CurrentCulture.NumberFormat.CurrencySymbol;

    /// <summary>
    /// Colour classification of the loaded document, one character per page, or null until
    /// the background check has finished. Belongs to exactly one <see cref="Document"/>.
    /// </summary>
    private string? _colourMap;
    private string? _colourProblem;
    private int _pagesChecked;
    private CancellationTokenSource? _colourCheck;

    /// <summary>What the invoice will charge if every page prints, shown before anything is committed.</summary>
    public string InvoiceEstimate
    {
        get
        {
            if (Document is null) return "Add a PDF to see what the invoice will charge.";
            if (!TryReadPrices(out var blackWhite, out var colour, out var problem)) return problem!;

            var colourPages = 0;
            if (!BlackAndWhite)
            {
                if (_colourMap is null)
                    return _colourProblem ?? $"Checking which pages use colour…  {_pagesChecked} of {Document.PageCount}";
                colourPages = _colourMap.Count(c => c == PageColourMap.Colour);
            }

            var blackWhitePages = Document.PageCount - colourPages;
            var lines = new List<string>
            {
                $"Black & white  {blackWhitePages,6} × {Money(blackWhite),10}  = {Money(blackWhitePages * blackWhite),12}",
                $"Colour         {colourPages,6} × {Money(colour),10}  = {Money(colourPages * colour),12}",
                $"Total{"",31}{Money(blackWhitePages * blackWhite + colourPages * colour),12}",
            };

            if (BlackAndWhite)
            {
                lines.Add("");
                lines.Add("Printing in black & white, so every page is charged as black & white.");
            }
            else if (colourPages > 0)
            {
                var pages = Enumerable.Range(1, Document.PageCount)
                    .Where(p => PageColourMap.IsColour(_colourMap!, p)).ToList();
                lines.Add("");
                lines.Add($"Colour pages: {PageColourMap.Shorten(PageColourMap.DescribeRanges(pages), 160)}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Works out which pages carry colour, in the background, as soon as it is needed:
    /// a PDF is loaded with the invoice ticked. A few hundred pages take a few seconds; the
    /// estimate shows progress, and Start waits for the answer rather than guessing.
    /// </summary>
    private void RestartColourCheck()
    {
        _colourCheck?.Cancel();
        _colourCheck = null;
        _colourMap = null;
        _colourProblem = null;
        _pagesChecked = 0;

        if (!PrintInvoice || Document is null || BlackAndWhite) return;

        var document = Document;
        var check = new CancellationTokenSource();
        _colourCheck = check;

        // Created here, on the UI thread, so its reports arrive back on the UI thread.
        var progress = new Progress<int>(done =>
        {
            if (_colourCheck != check) return;
            _pagesChecked = done;
            Raise(nameof(InvoiceEstimate));
        });

        _ = RunColourCheckAsync(document, check, progress);
    }

    private async Task RunColourCheckAsync(PdfDocumentInfo document, CancellationTokenSource check, IProgress<int> progress)
    {
        try
        {
            var pages = await Task.Run(() => PageColourAnalyzer.Analyse(document.Path, progress, check.Token));
            if (_colourCheck != check) return;

            _colourMap = PageColourMap.Encode(pages);
            Log.Information("Colour check of {File}: {Colour} of {Pages} pages use colour",
                document.FileName, pages.Count(p => p), pages.Length);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (_colourCheck != check) return;
            _colourProblem = "The pages could not be checked for colour, so the invoice cannot be priced. " +
                             "Untick “Print an invoice” to print without one.";
            Log.Warning(ex, "Colour check failed for {File}", document.FileName);
        }
        finally
        {
            if (_colourCheck == check) _colourCheck = null;
            check.Dispose();
        }

        Raise(nameof(InvoiceEstimate));
        RefreshCanStart();
    }

    private bool TryReadPrices(out decimal blackWhite, out decimal colour, out string? problem)
    {
        colour = 0;
        problem = null;

        if (!TryReadPrice(BlackWhitePrice, out blackWhite))
        {
            problem = "Enter the price of a black & white page, for example 2 or 2.50.";
            return false;
        }

        if (!TryReadPrice(ColourPrice, out colour))
        {
            problem = "Enter the price of a colour page, for example 10 or 12.50.";
            return false;
        }

        return true;
    }

    private static bool TryReadPrice(string text, out decimal value)
    {
        var parsed =
            decimal.TryParse(text, NumberStyles.Currency, CultureInfo.CurrentCulture, out value) ||
            decimal.TryParse(text, NumberStyles.Currency, CultureInfo.InvariantCulture, out value);
        return parsed && value >= 0 && value <= 1_000_000m;
    }

    private static string FormatPrice(decimal value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    private static string Money(decimal value) => value.ToString("C", CultureInfo.CurrentCulture);

    private bool _canStart;
    public bool CanStart
    {
        get => _canStart;
        private set { if (Set(ref _canStart, value)) StartCommand.RaiseCanExecuteChanged(); }
    }

    private string? _setupProblem;
    public string? SetupProblem
    {
        get => _setupProblem;
        private set { if (Set(ref _setupProblem, value)) Raise(nameof(HasSetupProblem)); }
    }

    public bool HasSetupProblem => !string.IsNullOrWhiteSpace(SetupProblem);

    // ---- Dashboard ------------------------------------------------------------

    public bool ShowDashboard => _job is not null;
    public bool ShowSetup => _job is null;

    public string JobDocument => _job?.FileName ?? "";
    public string JobSubtitle => _job is null
        ? ""
        : $"{_job.TotalPages} pages  ·  {_job.BatchCount} batches of up to {_job.PagesPerBatch}  " +
          $"·  {_job.DelayMinutes} min apart  ·  {_job.ColourMode.Describe()}  ·  {_job.PrinterName}";

    public int PagesDone => _job?.PagesDone ?? 0;

    public int PagesTotal => _job?.TotalPages ?? 0;

    public double ProgressPercent => PagesTotal == 0 ? 0 : 100.0 * PagesDone / PagesTotal;

    public string ProgressText => _job is null ? "" : $"{PagesDone} / {PagesTotal} pages";

    public string StateHeadline => _job?.State switch
    {
        JobState.Draft => "Ready to start",
        JobState.WaitingForStart => "Waiting for the start time",
        JobState.PreparingBatch => "Preparing the next batch",
        JobState.PrintingBatch => "Printing",
        JobState.VerifyingBatch => "Checking what printed",
        JobState.CoolingDown => "Resting the printer",
        JobState.BlockedPrinterOffline => "Printer unavailable",
        JobState.BlockedPrinterError => "Printer needs attention",
        JobState.Paused => "Paused",
        JobState.NeedsAttention => "Needs your decision",
        JobState.Completed => "Finished",
        JobState.Failed => "Failed",
        JobState.Cancelled => "Cancelled",
        _ => "",
    };

    public string StateDetail => _job?.AttentionReason ?? _job?.LastPrinterStatus ?? "";

    public Brush StateForeground => Resource(_job?.State switch
    {
        JobState.Completed => "Success",
        JobState.NeedsAttention or JobState.BlockedPrinterError => "Warning",
        JobState.Failed => "Danger",
        JobState.Cancelled => "TextSecondary",
        JobState.CoolingDown or JobState.WaitingForStart => "Waiting",
        JobState.Paused => "TextSecondary",
        _ => "Accent",
    });

    public Brush StateBackground => Resource(_job?.State switch
    {
        JobState.Completed => "SuccessSoft",
        JobState.NeedsAttention or JobState.BlockedPrinterError => "WarningSoft",
        JobState.Failed => "DangerSoft",
        JobState.CoolingDown or JobState.WaitingForStart => "WaitingSoft",
        JobState.Cancelled or JobState.Paused => "SurfaceMuted",
        _ => "AccentSoft",
    });

    public string CurrentBatchText
    {
        get
        {
            if (_job is null) return "";
            var batch = _job.CurrentBatch;
            return batch is null
                ? "—"
                : $"Batch {batch.Number} of {_job.BatchCount}  ·  {batch.RangeLabel}";
        }
    }

    public string NextBatchText
    {
        get
        {
            if (_job is null) return "";
            if (_job.IsTerminal) return "—";
            var next = _job.NextPendingBatch;
            return next is null ? "Nothing left to send" : $"Batch {next.Number}  ·  {next.RangeLabel}";
        }
    }

    private string _countdown = "";
    public string Countdown
    {
        get => _countdown;
        private set { if (Set(ref _countdown, value)) Raise(nameof(HasCountdown)); }
    }

    public bool HasCountdown => !string.IsNullOrEmpty(Countdown);

    private string _countdownCaption = "Next batch in";
    public string CountdownCaption
    {
        get => _countdownCaption;
        private set => Set(ref _countdownCaption, value);
    }

    public ObservableCollection<BatchViewModel> Batches { get; } = new();
    public ObservableCollection<string> RecentEvents { get; } = new();

    public bool CanPause => _job is { IsTerminal: false } && _job.State != JobState.Paused;
    public bool CanResume => _job is { State: JobState.Paused or JobState.NeedsAttention
        or JobState.BlockedPrinterOffline or JobState.BlockedPrinterError or JobState.Failed };
    public bool CanCancel => _job is { IsTerminal: false };
    public bool IsFinished => _job is { IsTerminal: true };
    public bool IsPrintingNow => _job?.State == JobState.PrintingBatch;

    /// <summary>
    /// Honest wording about what pause can and cannot do. Windows will not un-print pages
    /// that have already been handed to the printer, and the application does not pretend
    /// otherwise.
    /// </summary>
    public string PauseExplanation => IsPrintingNow
        ? "Pause stops the next batch from being sent. The batch already in the printer's queue will finish — " +
          "Windows cannot reliably un-send pages that have already reached the printer. " +
          "Use “Cancel queued batch” if you need to stop that too."
        : "Pause stops the next batch from being sent. Nothing is currently in the printer's queue.";

    // ---- Invoice (dashboard) -------------------------------------------------------

    public bool ShowInvoice => _job?.WantsInvoice == true;

    public string InvoiceStatus => _job?.InvoiceState switch
    {
        InvoiceState.Waiting when _job.IsTerminal =>
            "Not printed, because the job did not finish. You can still print it for the pages that came out.",
        InvoiceState.Waiting => "Prints automatically after the last batch.",
        InvoiceState.Sending => "Printing the invoice…",
        InvoiceState.Printed => $"Printed at {_job.InvoicePrintedUtc?.ToLocalTime():HH:mm}.",
        InvoiceState.Failed => "The printer did not accept the invoice, so nothing printed. You can send it again.",
        InvoiceState.Interrupted =>
            "PrintPacer stopped while sending the invoice. Check the printer's tray before printing it again.",
        _ => "",
    };

    public string InvoiceCharges
    {
        get
        {
            if (_job?.Invoice is not { } invoice) return "";
            return string.Join(Environment.NewLine,
                $"Black & white  {invoice.BlackWhitePages,6} × {Money(invoice.BlackWhitePagePrice),10}  = {Money(invoice.BlackWhiteAmount),12}",
                $"Colour         {invoice.ColourPages,6} × {Money(invoice.ColourPagePrice),10}  = {Money(invoice.ColourAmount),12}",
                $"{(_job.IsTerminal ? "Total" : "Total so far"),-36}{Money(invoice.Total),12}");
        }
    }

    public bool CanPrintInvoice => _job is { IsTerminal: true, WantsInvoice: true } &&
                                   _job.InvoiceState != InvoiceState.Sending;

    public string PrintInvoiceLabel => _job?.InvoiceState is InvoiceState.Printed or InvoiceState.Interrupted
        ? "Print invoice again"
        : "Print invoice";

    private void PrintInvoiceNow()
    {
        if (_job is not { } job || !CanPrintInvoice) return;

        // A second copy of a bill is harmless but still worth a moment's thought, and after an
        // interruption the first one may well be sitting in the tray.
        if (job.InvoiceState is InvoiceState.Printed or InvoiceState.Interrupted)
        {
            var answer = MessageBox.Show(
                job.InvoiceState == InvoiceState.Printed
                    ? "The invoice has already printed. Print another copy?"
                    : "The invoice may already have printed. Please check the printer's tray.\n\nPrint it now?",
                "Print invoice", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        _engine.PrintInvoice(job.Id);
    }

    // ---- Printer status strip ---------------------------------------------------

    private string _printerStatus = "Checking…";
    public string PrinterStatus
    {
        get => _printerStatus;
        private set => Set(ref _printerStatus, value);
    }

    private string? _deviceStatus;
    public string? DeviceStatus
    {
        get => _deviceStatus;
        private set { if (Set(ref _deviceStatus, value)) Raise(nameof(HasDeviceStatus)); }
    }

    public bool HasDeviceStatus => !string.IsNullOrWhiteSpace(DeviceStatus);

    private Brush _printerStatusBrush = Brushes.Gray;
    public Brush PrinterStatusBrush
    {
        get => _printerStatusBrush;
        private set => Set(ref _printerStatusBrush, value);
    }

    public string PrinterHint
    {
        get
        {
            if (SelectedPrinter is null) return "";
            var snapshot = _spooler.GetPrinter(SelectedPrinter);
            return snapshot.PortName is { Length: > 0 } port ? $"{snapshot.DriverName}  ·  {port}" : "";
        }
    }

    // ---- Actions -------------------------------------------------------------------

    public void LoadDocument(string path)
    {
        try
        {
            Document = _pdf.Load(path);
            SetupProblem = null;
            Log.Information("Loaded {File}: {Pages} pages", Document.FileName, Document.PageCount);
        }
        catch (PdfLoadException ex)
        {
            Document = null;
            SetupProblem = ex.Message;
            Log.Warning("Rejected {Path}: {Message}", path, ex.Message);
        }
        catch (Exception ex)
        {
            Document = null;
            SetupProblem = $"That file could not be opened.\n\n{ex.Message}";
            Log.Error(ex, "Unexpected failure loading {Path}", path);
        }
    }

    private void Browse()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a PDF to print",
            Filter = "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true) LoadDocument(dialog.FileName);
    }

    private async void Start()
    {
        if (Document is null || SelectedPrinter is null) return;
        if (!TryReadPagesPerBatch(out var perBatch, out _)) return;
        if (!TryReadDelay(out var delay, out _)) return;

        var start = ResolveStart(out var problem);
        if (problem is not null) { SetupProblem = problem; return; }

        InvoiceRequest? invoice = null;
        if (PrintInvoice)
        {
            if (!TryReadPrices(out var blackWhite, out var colour, out _)) return;
            if (!BlackAndWhite && _colourMap is null) return;
            invoice = new InvoiceRequest(blackWhite, colour, _colourMap);
        }

        try
        {
            var job = await _engine.CreateJobAsync(
                Document, SelectedPrinter, perBatch, delay,
                StartLater ? StartMode.AtTime : StartMode.Immediately,
                start?.ToUniversalTime(),
                Settings.RenderDpi, ColourMode, invoice);

            Settings.DefaultPagesPerBatch = perBatch;
            Settings.DefaultDelayMinutes = delay;
            Settings.LastPrinterName = SelectedPrinter;
            Settings.ColourMode = ColourMode;
            Settings.PrintInvoice = PrintInvoice;
            if (invoice is not null)
            {
                Settings.BlackWhitePagePrice = invoice.BlackWhitePagePrice;
                Settings.ColourPagePrice = invoice.ColourPagePrice;
            }
            _settingsStore.Save(Settings);

            _engine.StartJob(job.Id);
        }
        catch (Exception ex)
        {
            SetupProblem = $"The job could not be created.\n\n{ex.Message}";
            Log.Error(ex, "CreateJob failed");
        }
    }

    private void CancelJob()
    {
        if (_job is null) return;

        var remaining = _job.PendingBatchCount;
        var answer = MessageBox.Show(
            $"Cancel printing {_job.FileName}?\n\n" +
            $"{PagesDone} of {PagesTotal} pages have printed so far. " +
            $"{remaining} batch(es) will not be sent.\n\n" +
            "Anything already in the printer's queue will still finish.",
            "Cancel job", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes) _engine.CancelJob(_job.Id);
    }

    private void NewJob()
    {
        if (_job is { IsTerminal: false }) return;

        if (_job is not null)
        {
            _engine.DeleteJob(_job.Id);
            _job = null;
        }

        Document = null;
        SetupProblem = null;
        RefreshAll();
    }

    /// <summary>
    /// Resending pages that may already be on paper is the one genuinely destructive thing
    /// this application can do, so it always asks, and says exactly what it is about to do.
    /// </summary>
    private static bool ConfirmResend(BatchViewModel batch)
    {
        var answer = MessageBox.Show(
            $"Send {batch.Range} to the printer again?\n\n" +
            (batch.PartialHint is { } hint
                ? $"The printer's own counter suggests {hint}. Anything already printed will print a second time.\n\n"
                : "If those pages did print, you will get a second copy of them.\n\n") +
            "Please check the output tray before continuing.",
            "Resend batch", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }

    private void OpenPrintQueue()
    {
        if (SelectedPrinter is null && _job is null) return;
        var printer = _job?.PrinterName ?? SelectedPrinter!;

        try
        {
            // rundll32 printui is the documented way to open a specific queue's window.
            Process.Start(new ProcessStartInfo("rundll32.exe",
                $"printui.dll,PrintUIEntry /o /n \"{printer}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open the print queue window");
            OpenPath("shell:PrintersFolder");
        }
    }

    private void OpenPrinterSettings() => OpenPath("ms-settings:printers");

    private static void OpenPath(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Target}", target);
        }
    }

    private async void ExportLog()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export the job log",
            Filter = "Text file (*.txt)|*.txt",
            FileName = _job is null
                ? $"printpacer-log-{DateTime.Now:yyyyMMdd-HHmm}.txt"
                : $"printpacer-{Path.GetFileNameWithoutExtension(_job.FileName)}-{DateTime.Now:yyyyMMdd-HHmm}.txt",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            // The engine assembles the report on its own loop, where it can read the full
            // per-attempt history that is deliberately not exposed to other threads.
            var report = await _engine.BuildReportAsync(_job?.Id);
            await File.WriteAllLinesAsync(dialog.FileName, report);
            OpenPath(dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Log export failed");
            MessageBox.Show($"The log could not be saved.\n\n{ex.Message}", "Export log",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- Engine plumbing -------------------------------------------------------------

    private async void AdoptExistingJob()
    {
        try
        {
            var jobs = await _engine.GetJobsAsync();
            _job = jobs.LastOrDefault();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not read existing jobs");
        }

        RefreshAll();
        RefreshEvents();
    }

    /// <summary>The job currently on screen, for callers outside the view model.</summary>
    public JobSnapshot? CurrentJob => _job;

    private void OnJobChanged(JobSnapshot job)
    {
        if (_job is null || _job.Id == job.Id) _job = job;
        else if (job.IsActive) _job = job;

        Log.Debug("UI adopted job {File} in state {State}; showing dashboard = {Dashboard}",
            job.FileName, job.State, ShowDashboard);

        RefreshAll();
    }

    private void RefreshAll()
    {
        RebuildBatches();

        foreach (var name in new[]
                 {
                     nameof(ShowDashboard), nameof(ShowSetup), nameof(JobDocument), nameof(JobSubtitle),
                     nameof(PagesDone), nameof(PagesTotal), nameof(ProgressPercent), nameof(ProgressText),
                     nameof(StateHeadline), nameof(StateDetail), nameof(StateForeground), nameof(StateBackground),
                     nameof(CurrentBatchText), nameof(NextBatchText), nameof(CanPause), nameof(CanResume),
                     nameof(CanCancel), nameof(IsFinished), nameof(IsPrintingNow), nameof(PauseExplanation),
                     nameof(ShowInvoice), nameof(InvoiceStatus), nameof(InvoiceCharges),
                     nameof(CanPrintInvoice), nameof(PrintInvoiceLabel),
                 })
        {
            Raise(name);
        }

        RefreshCanStart();
    }

    private void RebuildBatches()
    {
        Batches.Clear();
        if (_job is null) return;

        foreach (var batch in _job.Batches)
        {
            var isCurrent = batch.Number == _job.CurrentBatchNumber;
            string? live = null;

            if (isCurrent && batch.State == BatchState.InFlight)
            {
                live = batch.SpoolerPagesPrinted > 0
                    ? $"Printing · {batch.SpoolerPagesPrinted}/{batch.PageCount}"
                    : batch.LastSpoolerStatus ?? "Sending";
            }

            Batches.Add(new BatchViewModel(batch, isCurrent, live));
        }
    }

    private void RefreshEvents()
    {
        RecentEvents.Clear();
        foreach (var entry in LoadEvents(60).TakeLast(60))
        {
            var line = $"{entry.Utc.ToLocalTime():HH:mm:ss}  {entry.Message}";
            if (entry.Detail is { Length: > 0 }) line += $"  ({entry.Detail})";
            RecentEvents.Add(line);
        }
    }

    private IReadOnlyList<JobEvent> LoadEvents(int limit)
    {
        try { return App.Store?.LoadEvents(_job?.Id, limit) ?? Array.Empty<JobEvent>(); }
        catch (Exception ex) { Log.Debug(ex, "Could not read the event log"); return Array.Empty<JobEvent>(); }
    }

    private void WithJob(Action<JobSnapshot> action)
    {
        if (_job is not null) action(_job);
    }

    // ---- Ticking ----------------------------------------------------------------------

    private void Tick()
    {
        UpdateCountdown();
        UpdatePrinterStatus();

        if (_job is { State: JobState.PrintingBatch })
        {
            // Page counts move while a batch is in flight, so refresh the rows that show them.
            RebuildBatches();
            Raise(nameof(PagesDone));
            Raise(nameof(ProgressPercent));
            Raise(nameof(ProgressText));
        }
    }

    private void UpdateCountdown()
    {
        if (_job is null || _job.IsTerminal)
        {
            Countdown = "";
            return;
        }

        var remaining = _engine.RemainingUntilNextBatch(_job.Id);
        switch (_job.State)
        {
            case JobState.CoolingDown:
                CountdownCaption = "Next batch in";
                Countdown = remaining is null ? "" : Deadline.Describe(remaining.Value);
                break;
            case JobState.WaitingForStart:
                CountdownCaption = "Starts in";
                Countdown = remaining is null ? "" : Deadline.Describe(remaining.Value);
                break;
            case JobState.BlockedPrinterOffline:
            case JobState.BlockedPrinterError:
                CountdownCaption = "Checking again in";
                Countdown = remaining is null ? "" : Deadline.Describe(remaining.Value);
                break;
            default:
                Countdown = "";
                break;
        }
    }

    private void UpdatePrinterStatus()
    {
        var printer = _job?.PrinterName ?? SelectedPrinter;
        if (printer is null) return;

        var snapshot = _spooler.GetPrinter(printer);
        PrinterStatus = snapshot.Exists
            ? $"{printer} — {snapshot.StatusText}"
            : $"{printer} — not installed";

        PrinterStatusBrush = Resource(snapshot.Availability switch
        {
            PrinterAvailability.Ready or PrinterAvailability.Busy => "Success",
            PrinterAvailability.Error or PrinterAvailability.NotFound => "Danger",
            PrinterAvailability.Offline or PrinterAvailability.Paused => "Warning",
            _ => "TextSecondary",
        });

        // The device is polled far less often than the queue: it is a network round trip,
        // and a printer being nagged every second is a printer that never sleeps.
        var now = DateTimeOffset.UtcNow;
        if (now - _lastDeviceProbe < TimeSpan.FromSeconds(Math.Max(5, Settings.DevicePollSeconds))) return;
        _lastDeviceProbe = now;

        _ = ProbeDeviceAsync(printer);
    }

    private async Task ProbeDeviceAsync(string printer)
    {
        try
        {
            var probe = await _probes.GetProbeAsync(printer, CancellationToken.None);
            if (!probe.IsConfigured)
            {
                DeviceStatus = null;
                return;
            }

            var snapshot = await probe.ProbeAsync(CancellationToken.None);
            var text = snapshot.Reachable
                ? $"Printer reports: {snapshot.Describe()}"
                : "Printer not answering on the network (it may be asleep)";

            if (snapshot.HasVerificationCounter)
                text += $"  ·  page counter {snapshot.VerificationCounter}";

            // BeginInvoke returns an operation handle; nothing here needs to wait for it.
            _ = _dispatcher.BeginInvoke(() => DeviceStatus = text);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Device status refresh failed");
        }
    }

    // ---- Validation ---------------------------------------------------------------------

    private void RefreshCanStart()
    {
        if (_job is not null) { CanStart = false; return; }

        if (Document is null) { CanStart = false; SetupProblem = null; return; }
        if (SelectedPrinter is null) { CanStart = false; SetupProblem = "Choose a printer."; return; }

        if (!TryReadPagesPerBatch(out _, out var pagesProblem)) { CanStart = false; SetupProblem = pagesProblem; return; }
        if (!TryReadDelay(out _, out var delayProblem)) { CanStart = false; SetupProblem = delayProblem; return; }

        ResolveStart(out var startProblem);
        if (startProblem is not null) { CanStart = false; SetupProblem = startProblem; return; }

        if (PrintInvoice)
        {
            if (!TryReadPrices(out _, out _, out var priceProblem)) { CanStart = false; SetupProblem = priceProblem; return; }

            // Still checking pages for colour: the estimate shows progress, so there is no
            // problem to report, but the job cannot be priced until the check is done.
            if (!BlackAndWhite && _colourMap is null) { CanStart = false; SetupProblem = _colourProblem; return; }
        }

        SetupProblem = null;
        CanStart = true;
    }

    private bool TryReadPagesPerBatch(out int value, out string? problem)
    {
        problem = null;
        if (!int.TryParse(PagesPerBatch, out value) || value < 1)
        {
            problem = "Pages per batch must be a whole number of at least 1.";
            return false;
        }

        if (Document is not null && value > Document.PageCount)
        {
            // Not an error: the document simply becomes a single batch.
            value = Document.PageCount;
        }

        return true;
    }

    private bool TryReadDelay(out int value, out string? problem)
    {
        problem = null;
        if (!int.TryParse(DelayMinutes, out value) || value < 0)
        {
            problem = "The delay must be a whole number of minutes (0 or more).";
            return false;
        }

        if (value > 24 * 60)
        {
            problem = "The delay cannot be longer than 24 hours.";
            return false;
        }

        return true;
    }

    private DateTimeOffset? ResolveStart(out string? problem)
    {
        problem = null;
        if (!StartLater) return null;

        if (!TimeSpan.TryParse(StartTime, out var time) || time.TotalHours >= 24)
        {
            problem = "Enter the start time as HH:mm, for example 23:30.";
            return null;
        }

        var start = new DateTimeOffset(StartDate.Date + time, DateTimeOffset.Now.Offset);
        if (start <= DateTimeOffset.Now.AddSeconds(30))
        {
            problem = "The start time is in the past. Pick a later time, or start immediately.";
            return null;
        }

        return start;
    }

    public void RefreshPrinters()
    {
        var previous = SelectedPrinter;
        Printers.Clear();

        // A driver may have been changed or updated since it was last asked.
        _monochromeChecks.Clear();

        foreach (var name in _spooler.ListPrinterNames().OrderBy(n => n))
            Printers.Add(name);

        // Preference order: what was already chosen, then what the user chose last time,
        // then the Windows default printer. Falling back to the alphabetically first queue
        // is how someone ends up sending a textbook to the fax driver.
        SelectedPrinter =
            previous is not null && Printers.Contains(previous) ? previous :
            Settings.LastPrinterName is { } remembered && Printers.Contains(remembered) ? remembered :
            _spooler.GetDefaultPrinterName() is { } fallback && Printers.Contains(fallback) ? fallback :
            Printers.FirstOrDefault();
    }

    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        _settingsStore.Save(settings);
        _engine.UpdateSettings(settings);
        _probes.InvalidateAll();
        _lastDeviceProbe = DateTimeOffset.MinValue;
        RefreshCanStart();
    }

    public void Stop() => _ticker.Stop();

    private static Brush Resource(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
