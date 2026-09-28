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
/// The whole user interface, in one view model: the print queue (MainViewModel.Queue.cs), and
/// the selected job's setup screen or job screen (this file).
///
/// It deliberately owns no job state of its own: everything shown is projected from the
/// engine's persisted jobs whenever the engine says one changed. That
/// keeps the screen and the database incapable of disagreeing, which matters for an
/// application whose main job is to be believed about what has and has not printed. Even the
/// settings being edited on the setup screen are saved to their job as they change.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
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

        _intake = new PdfIntake(pdf);

        // Settings typed on the setup screen reach the job once the typing pauses, not on
        // every keystroke, and never while a half-typed number cannot be read.
        _applyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _applyTimer.Tick += (_, _) => _ = ApplyPendingEditAsync();

        _pagesPerBatch = settings.DefaultPagesPerBatch.ToString();
        _delayMinutes = settings.DefaultDelayMinutes.ToString();
        _colourMode = settings.ColourMode;
        _renderDpi = settings.RenderDpi;
        _printInvoice = settings.PrintInvoice;
        _blackWhitePrice = FormatPrice(settings.BlackWhitePagePrice);
        _colourPrice = FormatPrice(settings.ColourPagePrice);

        // Page selection, layout and sides always start plain; the details of the last booklet
        // or brochure are offered again.
        var layout = settings.LayoutDefaults ?? PrintSettings.Default;
        _paper = layout.Paper;
        _marginMm = FormatMm(layout.MarginMm);
        _foldGapMm = FormatMm(layout.FoldGapMm);
        _scaling = layout.Scaling;
        _binding = layout.Binding;
        _sheetsPerSection = layout.SheetsPerSignature;
        _keepBackCover = layout.KeepLastPageAsBackCover;
        _fold = layout.Fold;

        BrowseCommand = new RelayCommand(Browse);
        InitialiseQueueCommands();
        PauseCommand = new RelayCommand(() => WithJob(j => _engine.PauseJob(j.Id)));
        ResumeCommand = new RelayCommand(() => WithJob(j => _engine.ResumeJob(j.Id)));
        CancelCommand = new RelayCommand(CancelJob);
        OpenQueueCommand = new RelayCommand(OpenPrintQueue);
        OpenPrinterSettingsCommand = new RelayCommand(OpenPrinterSettings);
        OpenLogFolderCommand = new RelayCommand(() => OpenPath(AppPaths.LogDirectory));
        ExportLogCommand = new RelayCommand(ExportLog);
        RefreshPrintersCommand = new RelayCommand(RefreshPrinters);
        CancelQueuedBatchCommand = new RelayCommand(() => WithJob(j => _engine.CancelCurrentSpoolerJob(j.Id)));
        PrintInvoiceCommand = new RelayCommand(PrintInvoiceNow);
        PreviewLayoutCommand = new RelayCommand(PreviewLayout, () => CanPreviewLayout);

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

        LoadJobs();
    }

    public AppSettings Settings { get; private set; }

    // ---- Commands -----------------------------------------------------------

    public RelayCommand BrowseCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand CancelCommand { get; }
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
    public RelayCommand PreviewLayoutCommand { get; }

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
            RefreshPrintSettings();

            // The job's printer, not the default for new jobs: that changes only in Settings,
            // or with "Use as defaults".
            if (value is not null) _lastDeviceProbe = DateTimeOffset.MinValue;
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
            RefreshPrintSettings();
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

    public IReadOnlyList<Choice<int>> RenderDpiChoices { get; } = new[]
    {
        new Choice<int>(150, "150 dpi (fastest)"),
        new Choice<int>(200, "200 dpi"),
        new Choice<int>(300, "300 dpi (standard)"),
        new Choice<int>(400, "400 dpi"),
        new Choice<int>(600, "600 dpi (slowest)"),
    };

    private int _renderDpi;

    /// <summary>The job's render resolution. New jobs start at the default from Settings.</summary>
    public int RenderDpi
    {
        get => _renderDpi;
        set { if (Set(ref _renderDpi, value)) RefreshCanStart(); }
    }

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

    // ---- Print settings: what to print, and how it goes on the paper -----------------------
    //
    // The first stages of the pipeline. Page selection and layout decide which sheets exist;
    // only then does the batch size divide those sheets into print jobs.

    /// <summary>An entry in one of the print-settings lists.</summary>
    public sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<Choice<PageSelection>> PageSelections { get; } = new[]
    {
        new Choice<PageSelection>(PageSelection.AllPages, "All pages"),
        new Choice<PageSelection>(PageSelection.OddPages, "Odd pages only (1, 3, 5 …)"),
        new Choice<PageSelection>(PageSelection.EvenPages, "Even pages only (2, 4, 6 …)"),
    };

    public IReadOnlyList<Choice<PrintLayout>> Layouts { get; } = new[]
    {
        new Choice<PrintLayout>(PrintLayout.Normal, "Normal: one page per side"),
        new Choice<PrintLayout>(PrintLayout.Booklet, "Booklet: fold and nest the sheets"),
        new Choice<PrintLayout>(PrintLayout.Brochure, "Brochure: folded leaflets"),
    };

    public IReadOnlyList<Choice<bool>> SidesChoices { get; } = new[]
    {
        new Choice<bool>(false, "Single-sided"),
        new Choice<bool>(true, "Double-sided"),
    };

    public IReadOnlyList<Choice<DuplexMode>> FlipChoices { get; } = new[]
    {
        new Choice<DuplexMode>(DuplexMode.LongEdge, "Long edge: turn like a book"),
        new Choice<DuplexMode>(DuplexMode.ShortEdge, "Short edge: flip up like a notepad"),
    };

    public IReadOnlyList<Choice<PaperChoice>> Papers { get; } = Enum.GetValues<PaperChoice>()
        .Select(paper => new Choice<PaperChoice>(paper, paper.Describe()))
        .ToList();

    public IReadOnlyList<Choice<BookletBinding>> Bindings { get; } = new[]
    {
        new Choice<BookletBinding>(BookletBinding.Left, "Left (left-to-right text)"),
        new Choice<BookletBinding>(BookletBinding.Right, "Right (right-to-left text)"),
    };

    public IReadOnlyList<Choice<int>> SectionChoices { get; } = new[] { 0, 1, 2, 3, 4, 5, 6, 8, 10, 12 }
        .Select(n => new Choice<int>(n, n == 0 ? "All sheets in one booklet" : $"{n} sheet{(n == 1 ? "" : "s")} ({n * 4} pages) per section"))
        .ToList();

    public IReadOnlyList<Choice<BrochureFold>> Folds { get; } = new[]
    {
        new Choice<BrochureFold>(BrochureFold.BiFold, "Bi-fold: 2 panels per side, 4 pages"),
        new Choice<BrochureFold>(BrochureFold.TriFold, "Tri-fold: 3 panels per side, 6 pages"),
    };

    public IReadOnlyList<Choice<PageScaling>> Scalings { get; } = new[]
    {
        new Choice<PageScaling>(PageScaling.FitToPanel, "Fit each page to its panel"),
        new Choice<PageScaling>(PageScaling.ShrinkOnly, "Actual size (shrink only if too big)"),
    };

    private PageSelection _pageSelection = PageSelection.AllPages;
    public PageSelection PageSelection
    {
        get => _pageSelection;
        set { if (Set(ref _pageSelection, value)) RefreshPrintSettings(); }
    }

    private PrintLayout _layout = PrintLayout.Normal;
    private bool _normalDoubleSided;
    private DuplexMode _normalFlip = DuplexMode.LongEdge;

    public PrintLayout Layout
    {
        get => _layout;
        set
        {
            var wasImposed = _layout != PrintLayout.Normal;
            if (!Set(ref _layout, value)) return;

            if (IsImposed && !wasImposed)
            {
                // A booklet or brochure has something on both sides of every sheet, and a
                // landscape sheet folded down the middle turns over on its short edge.
                _normalDoubleSided = _doubleSided;
                _normalFlip = _flip;
                _doubleSided = true;
                _flip = DuplexMode.ShortEdge;
            }
            else if (!IsImposed && wasImposed)
            {
                _doubleSided = _normalDoubleSided;
                _flip = _normalFlip;
            }

            RaiseLayoutProperties();
            RefreshPrintSettings();
        }
    }

    private void RaiseLayoutProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(Layout), nameof(IsNormalLayout), nameof(IsBooklet), nameof(IsBrochure), nameof(IsImposed),
                     nameof(CanChooseSides), nameof(DoubleSided), nameof(Flip), nameof(ShowFlip),
                     nameof(BatchSizeLabel),
                 })
        {
            Raise(name);
        }
    }

    public bool IsNormalLayout => Layout == PrintLayout.Normal;
    public bool IsBooklet => Layout == PrintLayout.Booklet;
    public bool IsBrochure => Layout == PrintLayout.Brochure;
    public bool IsImposed => Layout != PrintLayout.Normal;

    /// <summary>Booklets and brochures are always double-sided; the choice is locked, not hidden.</summary>
    public bool CanChooseSides => !IsImposed;

    private bool _doubleSided;
    public bool DoubleSided
    {
        get => _doubleSided;
        set
        {
            if (!Set(ref _doubleSided, value)) return;
            Raise(nameof(ShowFlip));
            Raise(nameof(BatchSizeLabel));
            RefreshPrintSettings();
        }
    }

    public bool ShowFlip => DoubleSided;

    private DuplexMode _flip = DuplexMode.LongEdge;
    public DuplexMode Flip
    {
        get => _flip;
        set { if (value != DuplexMode.SingleSided && Set(ref _flip, value)) RefreshPrintSettings(); }
    }

    private PaperChoice _paper;
    public PaperChoice Paper
    {
        get => _paper;
        set { if (Set(ref _paper, value)) RefreshPrintSettings(); }
    }

    private string _marginMm;
    public string MarginMm
    {
        get => _marginMm;
        set { if (Set(ref _marginMm, value)) RefreshPrintSettings(); }
    }

    private string _foldGapMm;
    public string FoldGapMm
    {
        get => _foldGapMm;
        set { if (Set(ref _foldGapMm, value)) RefreshPrintSettings(); }
    }

    private PageScaling _scaling;
    public PageScaling Scaling
    {
        get => _scaling;
        set { if (Set(ref _scaling, value)) RefreshPrintSettings(); }
    }

    private BookletBinding _binding;
    public BookletBinding Binding
    {
        get => _binding;
        set { if (Set(ref _binding, value)) RefreshPrintSettings(); }
    }

    private int _sheetsPerSection;
    public int SheetsPerSection
    {
        get => _sheetsPerSection;
        set { if (Set(ref _sheetsPerSection, value)) RefreshPrintSettings(); }
    }

    private bool _keepBackCover;
    public bool KeepBackCover
    {
        get => _keepBackCover;
        set { if (Set(ref _keepBackCover, value)) RefreshPrintSettings(); }
    }

    private BrochureFold _fold;
    public BrochureFold Fold
    {
        get => _fold;
        set
        {
            if (!Set(ref _fold, value)) return;
            Raise(nameof(BrochurePanelOrder));
            RefreshPrintSettings();
        }
    }

    public string SidesHint => IsImposed
        ? "Booklets and brochures have pages on both sides of every sheet."
        : DoubleSided
            ? "Long edge suits portrait pages; short edge suits landscape pages and top-bound documents."
            : "";

    public string FlipHint => IsImposed
        ? "Short edge is right for a sheet folded down the middle. Change it only if a test print shows the backs upside down."
        : "";

    public string BookletNote => SheetsPerSection > 0
        ? $"Sheets print outermost first. Fold each section of {SheetsPerSection} sheet(s) on its own, nesting each sheet inside the one before, then stack the sections in order."
        : "Sheets print outermost first: sheet 1 is the cover. Keep them in order, nest each sheet inside the one before, and fold.";

    public string BrochurePanelOrder => Fold == BrochureFold.TriFold
        ? "The PDF's pages are the panels, in this order: 1 front cover · 2 flap that folds in · 3, 4, 5 inside, left to right · 6 back cover. " +
          "Every further six pages make another leaflet. A PDF that already has whole sides laid out should be printed Normal, double-sided, short edge."
        : "The PDF's pages are the panels, in this order: 1 front cover · 2, 3 inside, left and right · 4 back cover. " +
          "Every further four pages make another leaflet. A PDF that already has whole sides laid out should be printed Normal, double-sided, short edge.";

    /// <summary>"Pages per batch" for plain jobs, as always; "Sheets per batch" once a sheet carries more than one page.</summary>
    public string BatchSizeLabel => !IsImposed && !DoubleSided ? "Pages per batch" : "Sheets per batch";

    public string BatchSizeHint => IsImposed || DoubleSided
        ? IsBooklet ? "1 sheet = 4 pages"
          : IsBrochure ? (Fold == BrochureFold.TriFold ? "1 sheet = 6 panels" : "1 sheet = 4 panels")
          : "1 sheet = 2 pages"
        : "";

    /// <summary>The print settings as chosen, or null with a reason when a field cannot be read.</summary>
    private PrintSettings? BuildPrintSettings(out string? problem)
    {
        problem = null;

        double margin = PrintSettings.Default.MarginMm, gap = PrintSettings.Default.FoldGapMm;
        if (IsImposed)
        {
            if (!TryReadMm(MarginMm, out margin) || margin > PrintSettings.MaxMarginMm)
            {
                problem = $"The margin must be a number of millimetres between 0 and {PrintSettings.MaxMarginMm:0}.";
                return null;
            }

            if (!TryReadMm(FoldGapMm, out gap) || gap > 2 * PrintSettings.MaxMarginMm)
            {
                problem = $"The space at the fold must be a number of millimetres between 0 and {2 * PrintSettings.MaxMarginMm:0}.";
                return null;
            }
        }

        var settings = new PrintSettings
        {
            PageSelection = PageSelection,
            Layout = Layout,
            Duplex = IsImposed || DoubleSided ? Flip : DuplexMode.SingleSided,
            Paper = IsImposed ? Paper : PaperChoice.PrinterDefault,
            MarginMm = IsImposed ? margin : PrintSettings.Default.MarginMm,
            FoldGapMm = IsImposed ? gap : PrintSettings.Default.FoldGapMm,
            Scaling = IsImposed ? Scaling : PageScaling.FitToPanel,
            Binding = IsBooklet ? Binding : BookletBinding.Left,
            SheetsPerSignature = IsBooklet ? SheetsPerSection : 0,
            KeepLastPageAsBackCover = IsBooklet && KeepBackCover,
            Fold = IsBrochure ? Fold : BrochureFold.BiFold,
        };

        problem = settings.Problems().FirstOrDefault();
        return problem is null ? settings : null;
    }

    /// <summary>The plan for the loaded document with the chosen settings, or null with a reason.</summary>
    private PrintPlan? BuildPlan(out string? problem)
    {
        problem = null;
        if (Document is null) return null;

        var settings = BuildPrintSettings(out problem);
        if (settings is null) return null;

        try
        {
            return PrintPlan.Build(settings, Document.PageCount);
        }
        catch (ArgumentException ex)
        {
            problem = ex.Message.Split(" (Parameter")[0];
            return null;
        }
    }

    /// <summary>What the driver said about each printer and set of sides, paper and orientation.</summary>
    private readonly Dictionary<string, PrinterSetupReport> _setupChecks = new();
    private readonly HashSet<string> _setupChecksRunning = new();

    private static string SetupKey(string printer, PrinterSetup setup, double? margin) =>
        $"{printer}|{setup}|{margin?.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The driver's verdict on the current choices: null while it is being asked, and also when
    /// there is nothing to ask (a plain job leaves every printer setting at its default, as always).
    /// </summary>
    private PrinterSetupReport? CurrentSetupReport(out bool pending)
    {
        pending = false;
        if (SelectedPrinter is not { } printer) return null;
        if (BuildPrintSettings(out _) is not { } settings) return null;

        var setup = settings.PrinterSetup;
        if (setup == PrinterSetup.Default) return null;

        var margin = settings.IsImposed ? settings.MarginMm : (double?)null;
        return SetupReportFor(printer, setup, margin, out pending);
    }

    /// <summary>The driver's verdict on one printer and setup, asked once and then remembered.</summary>
    private PrinterSetupReport? SetupReportFor(string printer, PrinterSetup setup, double? margin, out bool pending)
    {
        pending = false;
        var key = SetupKey(printer, setup, margin);
        if (_setupChecks.TryGetValue(key, out var report)) return report;

        pending = true;
        if (_setupChecksRunning.Add(key))
        {
            // Asking a driver can take a moment on a network printer, so keep it off the UI
            // thread. The check never throws.
            _ = Task.Run(() =>
            {
                var result = PrinterSetupCheck.Check(printer, setup, margin);
                _dispatcher.BeginInvoke(() =>
                {
                    _setupChecksRunning.Remove(key);
                    _setupChecks[key] = result;
                    RefreshPrintSettings();
                    SyncQueue();
                });
            });
        }
        return null;
    }

    /// <summary>
    /// Under the print settings: whether the chosen printer can do what was asked, in words.
    /// Problems name the setting to change; nothing is ever shown as applied that the driver refused.
    /// </summary>
    public string? PrinterSetupNote
    {
        get
        {
            var report = CurrentSetupReport(out var pending);
            if (pending) return "Checking the printer's settings…";
            if (report is null) return null;

            var lines = report.Issues.Select(i => (i.Blocking ? "✕ " : "! ") + i.Message).ToList();

            if (report.CanPrint && (IsImposed || DoubleSided))
            {
                lines.Add($"“{SelectedPrinter}” accepts double-sided printing. If it has no automatic duplex unit, " +
                          "its driver may stop part-way through each batch and ask someone to turn the paper over.");
            }

            if (report.CanPrint && IsImposed && report.PaperName is { } paper)
                lines.Add($"Sheets: {paper}, landscape.");

            return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
        }
    }

    public Brush PrinterSetupNoteBrush =>
        CurrentSetupReport(out _) is { } report && report.Issues.Count > 0
            ? Resource("Warning")
            : Resource("TextSecondary");

    /// <summary>The first few sheets, side by side, so a booklet or brochure can be checked before printing.</summary>
    public string? SheetMap
    {
        get
        {
            var plan = BuildPlan(out _);
            if (plan is null || plan.Unit == PrintUnit.Page) return null;
            return string.Join(Environment.NewLine, plan.DescribeSheets(4));
        }
    }

    public bool ShowPrintDetails => IsImposed || DoubleSided || PageSelection != PageSelection.AllPages;

    private void RefreshPrintSettings()
    {
        foreach (var name in new[]
                 {
                     nameof(SidesHint), nameof(FlipHint), nameof(BookletNote), nameof(BatchSizeHint),
                     nameof(PrinterSetupNote), nameof(PrinterSetupNoteBrush), nameof(SheetMap),
                     nameof(ShowPrintDetails), nameof(BatchPreview), nameof(InvoiceEstimate),
                 })
        {
            Raise(name);
        }

        PreviewLayoutCommand.RaiseCanExecuteChanged();
        RefreshCanStart();
    }

    private static bool TryReadMm(string text, out double value) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
         double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        && double.IsFinite(value) && value >= 0;

    private static string FormatMm(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    // ---- Layout preview ----------------------------------------------------------------------

    private const string PreviewPrinter = "Microsoft Print to PDF";
    private const int PreviewSheets = 8;

    private bool _previewRunning;
    private bool _hasPreviewPrinter;
    private string? _previewStatus;

    public string? PreviewStatus
    {
        get => _previewStatus;
        private set => Set(ref _previewStatus, value);
    }

    public bool CanPreviewLayout =>
        !_previewRunning && _hasPreviewPrinter && Document is not null && BuildPlan(out _) is not null;

    /// <summary>
    /// Prints the first few sheets through the very same print engine, to a PDF instead of
    /// paper, and opens it. Fronts and backs come out as consecutive pages. Nothing is sent
    /// to the chosen printer and no job is created.
    /// </summary>
    private async void PreviewLayout()
    {
        if (Document is not { } document || BuildPlan(out _) is not { } plan) return;

        _previewRunning = true;
        PreviewLayoutCommand.RaiseCanExecuteChanged();
        PreviewStatus = "Preparing the preview…";

        try
        {
            var last = Math.Min(plan.UnitCount, plan.Unit == PrintUnit.Page ? PreviewSheets * 2 : PreviewSheets);
            var directory = Path.Combine(Path.GetTempPath(), "PrintPacer");
            Directory.CreateDirectory(directory);
            var output = Path.Combine(directory, $"layout-preview-{DateTime.Now:yyyyMMdd-HHmmss}.pdf");

            var request = new PrintRequest
            {
                SourcePath = document.Path,
                FirstPage = 1,
                LastPage = last,
                Sides = plan.SidesFor(1, last),
                // The PDF printer has one side per page, so the preview is single-sided by design.
                Setup = plan.Settings.PrinterSetup with { Duplex = DuplexMode.SingleSided },
                Imposition = plan.Settings.IsImposed ? ImpositionOptions.From(plan.Settings) : null,
                PrinterName = PreviewPrinter,
                DocumentToken = $"PrintPacer layout preview {Guid.NewGuid():N}",
                RenderDpi = 150,
                ColourMode = ColourMode,
                OutputFilePath = output,
            };

            var result = await new PdfiumPrintEngine().SubmitAsync(request, CancellationToken.None);
            if (!result.Spooled)
            {
                PreviewStatus = $"The preview could not be made: {result.Error}";
                return;
            }

            // The PDF printer finishes writing the file shortly after spooling ends.
            for (var i = 0; i < 60 && !IsReadable(output); i++) await Task.Delay(250);

            PreviewStatus = $"Preview of the first {last} {plan.UnitName(last)}: {output}";
            OpenPath(output);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Layout preview failed");
            PreviewStatus = $"The preview could not be made: {ex.Message}";
        }
        finally
        {
            _previewRunning = false;
            PreviewLayoutCommand.RaiseCanExecuteChanged();
        }
    }

    private static bool IsReadable(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return stream.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>One batch in the schedule preview: "Batch 2", "Pages 51–100", "+1 h 00 min".</summary>
    public sealed record ScheduleRow(string Batch, string Range, string When);

    /// <summary>
    /// What pressing Start will do, in three short lines that can be taken in at a glance, with
    /// the batch-by-batch schedule kept for when it is asked for. <see cref="IsProblem"/> means
    /// the headline is the reason nothing can be planned yet.
    /// </summary>
    public sealed record PlanPreview(
        string Headline,
        string? Timing = null,
        string? Start = null,
        string? Layout = null,
        IReadOnlyList<ScheduleRow>? Schedule = null,
        string? ScheduleNote = null,
        bool IsProblem = false)
    {
        public bool HasSchedule => Schedule is { Count: > 0 };
    }

    /// <summary>
    /// A plain-language preview of the exact schedule, shown before anything is committed.
    /// The user should never have to guess what pressing the button will do.
    /// </summary>
    public PlanPreview BatchPreview
    {
        get
        {
            if (Document is null) return new PlanPreview("Add a PDF to see how it will be split.");
            if (BuildPlan(out var planProblem) is not { } plan) return new PlanPreview(planProblem!, IsProblem: true);
            if (!TryReadPagesPerBatch(out var perBatch, out var problem)) return new PlanPreview(problem!, IsProblem: true);
            if (!TryReadDelay(out var delay, out var delayProblem)) return new PlanPreview(delayProblem!, IsProblem: true);

            var batches = PrintJob.PlanBatches("preview", plan, perBatch);
            var start = ResolveStart(out var startProblem);
            if (startProblem is not null) return new PlanPreview(startProblem, IsProblem: true);

            var settings = plan.Settings;
            var mode = ColourMode.Describe().ToLowerInvariant();

            var headline = batches.Count == 1
                ? $"{plan.DescribeSize()}  ·  one batch"
                : $"{plan.DescribeSize()}  ·  {batches.Count} batches  ·  up to {perBatch} {plan.UnitName(perBatch)} per batch";

            var timing = batches.Count == 1
                ? $"Sent as one print job, in {mode}"
                : delay == 0
                    ? $"No rest between batches  ·  every batch in {mode}"
                    : $"{delay} minute{(delay == 1 ? "" : "s")} between batches  ·  every batch in {mode}";

            // A plain job's settings are all on the screen already; a booklet or brochure also has
            // things worked out from them - the paper the driver will use, how far pages shrink.
            var layout = settings.IsImposed
                ? $"{DescribeLayout(plan)}  ·  {settings.DescribeSpacing()}{DescribeScale(plan)}"
                : null;

            // In a queue the job starts when its turn comes, which nobody can know in advance, so
            // the batches are timed from the job's own start rather than given clock times.
            var schedule = batches.Count == 1
                ? null
                : batches.Select(b => new ScheduleRow($"Batch {b.Number}", b.RangeLabel, Offset(delay * (b.Number - 1))))
                    .ToList();

            return new PlanPreview(
                headline,
                timing,
                "Starts " + DescribeStart(start),
                layout,
                schedule,
                batches.Count > 1 && delay > 0
                    ? "Times count rests only; each batch also takes as long as the printer needs to print it."
                    : null);
        }
    }

    /// <summary>When the job being set up will start, as far as can honestly be said.</summary>
    private string DescribeStart(DateTimeOffset? notBefore)
    {
        var ahead = _snapshots.Values.Count(j => j.Id != _job?.Id && j.IsActive &&
                                                (_job is null || j.State != JobState.Queued || j.QueuePosition < _job.QueuePosition));
        var queued = ahead == 0 ? "" : $"after the {ahead} job(s) ahead of it in the queue";

        if (notBefore is { } at)
            return $"not before {at.LocalDateTime:ddd dd MMM HH:mm}" + (ahead == 0 ? "" : ", and " + queued);
        if (ahead > 0) return queued;
        return _job?.State == JobState.Queued ? "now" : "as soon as you press Start printing";
    }

    private static string Offset(int minutes) => minutes == 0
        ? "at the start"
        : minutes < 60 ? $"+{minutes} min" : $"+{minutes / 60} h {minutes % 60:00} min";

    private string DescribeLayout(PrintPlan plan)
    {
        var settings = plan.Settings;
        var paper = settings.Paper == PaperChoice.PrinterDefault &&
                    CurrentSetupReport(out _) is { PaperName: { } name }
            ? name
            : settings.Paper.Describe();

        return settings.Layout switch
        {
            PrintLayout.Booklet =>
                $"Booklet, {settings.Binding.Describe().ToLowerInvariant()}, 2 pages per side, {paper} landscape" +
                (settings.SheetsPerSignature > 0 ? $", sections of {settings.SheetsPerSignature} sheet(s)" : "") +
                (settings.KeepLastPageAsBackCover ? ", last page kept as back cover" : ""),
            PrintLayout.Brochure =>
                $"Brochure, {settings.Fold.Describe().ToLowerInvariant()}, one leaflet per sheet, {paper} landscape",
            _ => "Normal, one page per side, on the printer's paper",
        };
    }

    /// <summary>", pages at about 71%": how much the document's first page is reduced to fit its panel.</summary>
    private string DescribeScale(PrintPlan plan)
    {
        if (Document?.FirstPageWidthPt is not { } widthPt || Document.FirstPageHeightPt is not { } heightPt) return "";

        var paper = CurrentSetupReport(out _)?.PaperSize ?? ImpositionGeometry.NominalPaper(plan.Settings.Paper);
        if (paper is not { } portrait) return "";

        try
        {
            var box = ImpositionGeometry.PanelBoxes(ImpositionGeometry.Landscape(portrait), plan.PanelsPerSide,
                plan.Settings.MarginMm, plan.Settings.FoldGapMm)[0];
            var scale = ImpositionGeometry.ScaleFor(box, ImpositionGeometry.PointsToHundredths(widthPt, heightPt),
                plan.Settings.Scaling);
            return $", pages at about {scale * 100:0}%";
        }
        catch (ArgumentException ex)
        {
            return $" ({ex.Message})";
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

            // Only pages that will print are charged: an odd- or even-page job bills half the
            // document, and a booklet's blank padding bills nothing.
            var selected = BuildPlan(out _)?.SelectedPages ?? Enumerable.Range(1, Document.PageCount).ToList();

            var colourPages = 0;
            if (!BlackAndWhite)
            {
                if (_colourMap is null)
                    return _colourProblem ?? $"Checking which pages use colour…  {_pagesChecked} of {Document.PageCount}";
                colourPages = selected.Count(p => PageColourMap.IsColour(_colourMap, p));
            }

            var blackWhitePages = selected.Count - colourPages;
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
                var pages = selected.Where(p => PageColourMap.IsColour(_colourMap!, p)).ToList();
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

        // Already known - from when the file was added, or from its job - so not worked out again.
        if (_knownColourMaps.TryGetValue(Document.Sha256, out var known) && PageColourMap.IsValid(known, Document.PageCount))
        {
            _colourMap = known;
            return;
        }

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
            _knownColourMaps[document.Sha256] = _colourMap;
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

    // Every decimal a price has, so showing a job's price and saving it again never rounds it.
    private static string FormatPrice(decimal value) => value.ToString("0.##########", CultureInfo.CurrentCulture);

    private static string Money(decimal value) => value.ToString("C", CultureInfo.CurrentCulture);

    private bool _editorValid;

    /// <summary>Whether everything on the setup screen can be read and printed, so it can be saved to the job.</summary>
    public bool EditorValid
    {
        get => _editorValid;
        private set
        {
            if (!Set(ref _editorValid, value)) return;
            UseAsDefaultsCommand.RaiseCanExecuteChanged();
            CopyToOthersCommand.RaiseCanExecuteChanged();
        }
    }

    private string? _setupProblem;
    public string? SetupProblem
    {
        get => _setupProblem;
        private set { if (Set(ref _setupProblem, value)) Raise(nameof(HasSetupProblem)); }
    }

    public bool HasSetupProblem => !string.IsNullOrWhiteSpace(SetupProblem);

    /// <summary>
    /// The problem, under "What will happen", unless the plan's own headline already says it:
    /// a batch size that cannot be read is said once, not twice.
    /// </summary>
    public bool ShowSetupProblem => HasSetupProblem && SetupProblem != BatchPreview.Headline;

    // ---- Dashboard ------------------------------------------------------------

    /// <summary>The selected job has started, or finished: show its progress.</summary>
    public bool ShowDashboard => _job is { IsWaiting: false };

    /// <summary>The selected job has not started: show its settings, which can still change.</summary>
    public bool ShowSetup => _job is { IsWaiting: true };

    public string JobDocument => _job is null ? "" : $"{_job.Label}  ·  {_job.FileName}";
    public string JobSubtitle => _job is null
        ? ""
        : $"{_job.SizeSummary}  ·  {_job.BatchCount} batch{(_job.BatchCount == 1 ? "" : "es")} of up to {_job.PagesPerBatch} {_job.UnitName(_job.PagesPerBatch)}  " +
          $"·  {_job.DelayMinutes} min apart  ·  {_job.ColourMode.Describe()}  ·  {_job.PrinterName}";

    /// <summary>The job's page selection, layout and sides, when they are anything but the plain default.</summary>
    public string JobPrintSettings => _job is null || _job.PrintSettings == PrintSettings.Default
        ? ""
        : _job.PrintSettings.Summary() + (_job.PrintSettings.IsImposed ? $"  ·  {_job.PrintSettings.DescribeSpacing()}" : "");

    /// <summary>Print units done: pages for a plain job, sheets for a double-sided, booklet or brochure job.</summary>
    public int PagesDone => _job?.PagesDone ?? 0;

    public int PagesTotal => _job?.UnitCount ?? 0;

    public double ProgressPercent => PagesTotal == 0 ? 0 : 100.0 * PagesDone / PagesTotal;

    public string ProgressText => _job is null ? "" : $"{PagesDone} / {PagesTotal} {_job.UnitName(PagesTotal)}";

    public string StateHeadline => _job?.State switch
    {
        JobState.Draft => "Not started",
        JobState.Queued when _job.HasSentAnything => "Waiting to continue",
        JobState.Queued => "Waiting in the queue",
        JobState.WaitingForStart => "Waiting for the start time",
        JobState.PreparingBatch => "Preparing the next batch",
        JobState.PrintingBatch => "Printing",
        JobState.VerifyingBatch => "Checking what printed",
        JobState.CoolingDown => "Resting the printer",
        JobState.BlockedPrinterOffline => "Printer unavailable",
        JobState.BlockedPrinterError => "Printer needs attention",
        JobState.Paused => "Paused",
        JobState.NeedsAttention => QueueItemViewModel.Describe(_job),
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

    // A job waiting its turn is held back from the queue instead; pausing only applies once it has the printer.
    public bool CanPause => _job is { IsTerminal: false } &&
                            _job.State is not (JobState.Paused or JobState.Draft or JobState.Queued);
    public bool CanResume => _job is { State: JobState.Paused or JobState.NeedsAttention
        or JobState.BlockedPrinterOffline or JobState.BlockedPrinterError or JobState.Failed };
    public bool CanCancel => _job is { IsTerminal: false, IsWaiting: false };
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

    /// <summary>Adds one PDF to the queue, as "Open with PrintPacer" does.</summary>
    public void LoadDocument(string path) => AddFiles(new[] { path });

    /// <summary>Chooses one or several PDFs; each becomes its own job.</summary>
    private void Browse()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose PDFs to print",
            Filter = "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };

        if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
    }

    private void CancelJob()
    {
        if (_job is null) return;

        var remaining = _job.PendingBatchCount;
        var answer = MessageBox.Show(
            $"Cancel printing {_job.FileName}?\n\n" +
            $"{PagesDone} of {PagesTotal} {_job.UnitName(PagesTotal)} have printed so far. " +
            $"{remaining} batch(es) will not be sent.\n\n" +
            "Anything already in the printer's queue will still finish.",
            "Cancel job", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes) _engine.CancelJob(_job.Id);
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

    private async void LoadJobs()
    {
        try
        {
            foreach (var job in await _engine.GetJobsAsync()) Remember(job);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not read existing jobs");
        }

        SyncQueue();

        // After a restart, open on the job that is printing, or that needs a decision.
        var running = _snapshots.Values.OrderBy(j => j.QueuePosition)
            .FirstOrDefault(j => j.IsActive && j.State != JobState.Queued);
        if (running is not null) Select(running.Id);

        RefreshAll();
        RefreshEvents();
    }

    private void Remember(JobSnapshot job)
    {
        _snapshots[job.Id] = job;
        if (job.Configuration.Invoice?.PageColourMap is { } map && PageColourMap.IsValid(map, job.TotalPages))
            _knownColourMaps[job.FileSha256] = map;
    }

    /// <summary>The job currently on screen, for callers outside the view model.</summary>
    public JobSnapshot? CurrentJob => _job;

    private void OnJobChanged(JobSnapshot job)
    {
        // A job removed a moment ago can still have a change in flight; it must not come back.
        if (_removed.Contains(job.Id)) return;

        Remember(job);
        var selected = _job?.Id == job.Id;
        var wasWaiting = _job?.IsWaiting == true;
        if (selected) _job = job;

        SyncQueue();

        if (!selected) return;

        Log.Debug("UI refreshed job {File} in state {State}; showing dashboard = {Dashboard}",
            job.FileName, job.State, ShowDashboard);

        // The setup screen keeps what is being typed; the engine's copy is only shown when the
        // job is first selected. A job that has just started switches to its progress.
        if (wasWaiting && !job.IsWaiting) CancelPendingEdit();
        RefreshAll();
    }

    private void RefreshAll()
    {
        RebuildBatches();

        foreach (var name in new[]
                 {
                     nameof(ShowDashboard), nameof(ShowSetup), nameof(JobDocument), nameof(JobSubtitle),
                     nameof(JobPrintSettings),
                     nameof(PagesDone), nameof(PagesTotal), nameof(ProgressPercent), nameof(ProgressText),
                     nameof(StateHeadline), nameof(StateDetail), nameof(StateForeground), nameof(StateBackground),
                     nameof(CurrentBatchText), nameof(NextBatchText), nameof(CanPause), nameof(CanResume),
                     nameof(CanCancel), nameof(IsFinished), nameof(IsPrintingNow), nameof(PauseExplanation),
                     nameof(ShowInvoice), nameof(InvoiceStatus), nameof(InvoiceCharges),
                     nameof(CanPrintInvoice), nameof(PrintInvoiceLabel),
                     nameof(EditorJobLabel), nameof(EditorStatus), nameof(CanHold), nameof(BatchPreview),
                     nameof(SaveStatus), nameof(SaveNote),
                     nameof(QueueHeadline), nameof(StartQueueLabel),
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
                // Windows counts printed sides, which for a sheet-based job is not the sheet count.
                live = batch.SpoolerPagesPrinted > 0
                    ? _job.Unit == PrintUnit.Page
                        ? $"Printing · {batch.SpoolerPagesPrinted}/{batch.PageCount}"
                        : $"Printing · side {batch.SpoolerPagesPrinted}/{batch.Impressions}"
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
        RefreshEditorValidity();
        Raise(nameof(ShowSetupProblem));
        EditorChanged();
    }

    private void RefreshEditorValidity()
    {
        if (!ShowSetup) { EditorValid = false; SetupProblem = null; return; }

        if (Document is null) { EditorValid = false; SetupProblem = null; return; }
        if (SelectedPrinter is null) { EditorValid = false; SetupProblem = "Choose a printer."; return; }

        // What to print comes first: without a plan there is nothing to batch.
        if (BuildPlan(out var planProblem) is null) { EditorValid = false; SetupProblem = planProblem; return; }

        // A printer that refuses double-sided, the paper or landscape is reported here and on the
        // job's row, and the job is not started; the settings themselves are still saved, so the
        // user can pick another printer.
        var setupReport = CurrentSetupReport(out _);
        if (setupReport?.FirstBlocking is { } blocking)
            SetupProblem = $"{blocking.Setting}: {blocking.Message}";

        if (!TryReadPagesPerBatch(out _, out var pagesProblem)) { EditorValid = false; SetupProblem = pagesProblem; return; }
        if (!TryReadDelay(out _, out var delayProblem)) { EditorValid = false; SetupProblem = delayProblem; return; }

        ResolveStart(out var startProblem);
        if (startProblem is not null) { EditorValid = false; SetupProblem = startProblem; return; }

        if (PrintInvoice)
        {
            if (!TryReadPrices(out _, out _, out var priceProblem)) { EditorValid = false; SetupProblem = priceProblem; return; }

            // Still checking pages for colour: the estimate shows progress, so there is no
            // problem to report, but the job cannot be priced until the check is done.
            if (!BlackAndWhite && _colourMap is null) { EditorValid = false; SetupProblem = _colourProblem; return; }
        }

        if (setupReport?.FirstBlocking is null) SetupProblem = null;
        EditorValid = true;
    }

    private bool TryReadPagesPerBatch(out int value, out string? problem)
    {
        problem = null;
        if (!int.TryParse(PagesPerBatch, out value) || value < 1)
        {
            problem = !IsImposed && !DoubleSided
                ? "Pages per batch must be a whole number of at least 1."
                : "Sheets per batch must be a whole number of at least 1.";
            return false;
        }

        var units = BuildPlan(out _)?.UnitCount ?? Document?.PageCount;
        if (units is { } total && value > total)
        {
            // Not an error: the document simply becomes a single batch.
            value = total;
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
        _setupChecks.Clear();

        foreach (var name in _spooler.ListPrinterNames().OrderBy(n => n))
            Printers.Add(name);

        // A job keeps the printer it was given, even one since removed from Windows: the setup
        // screen shows it (with a warning on the job) rather than quietly switching printers.
        if (_job is { IsWaiting: true } job && !Printers.Contains(job.PrinterName)) Printers.Add(job.PrinterName);

        _hasPreviewPrinter = Printers.Contains(PreviewPrinter);
        PreviewLayoutCommand.RaiseCanExecuteChanged();

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
        SyncQueue();
    }

    public void Stop()
    {
        _ticker.Stop();
        FlushEdit();
    }

    private static Brush Resource(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
