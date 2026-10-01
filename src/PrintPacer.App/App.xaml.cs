using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PrintPacer.App.Platform;
using PrintPacer.App.Themes;
using PrintPacer.App.ViewModels;
using PrintPacer.App.Views;
using PrintPacer.Core.Device;
using PrintPacer.Core.Diagnostics;
using PrintPacer.Core.Engine;
using PrintPacer.Core.Model;
using PrintPacer.Core.Pdf;
using PrintPacer.Core.Persistence;
using PrintPacer.Core.Printing;
using PrintPacer.Core.Spooler;
using Serilog;

namespace PrintPacer.App;

/// <summary>
/// Composition root and process lifetime.
///
/// Two behaviours here exist specifically for unattended overnight use:
///
///   - Closing the window does not exit. A running job keeps going in the notification
///     area, because the most likely way to ruin an overnight print is to close the window
///     out of habit before going to bed.
///
///   - Only one instance may run. Two copies driving the same queue would each believe they
///     owned the in-flight batch, which is precisely how duplicate printing happens.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private JobStore? _store;
    private JobEngine? _engine;
    private DeviceProbeFactory? _probes;
    private TrayNotifier? _tray;
    private WindowsPowerManager? _power;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private bool _exiting;
    private IReadOnlyList<string> _pendingDocuments = Array.Empty<string>();

    /// <summary>Exposed for the view model's read-only access to the event log.</summary>
    internal static JobStore? Store { get; private set; }

    /// <summary>True when started with --data-dir, whose folder must never become the real one.</summary>
    internal static bool DataFolderIsSandbox { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The Start Menu's "data folder" shortcut. The folder can be moved, so the shortcut
        // asks rather than naming it. Opens Explorer and exits; never touches a running copy.
        if (e.Args.Contains("--open-data-folder", StringComparer.OrdinalIgnoreCase))
        {
            OpenFolder(DataFolder.CurrentRoot(new RegistryDataFolderChoice()));
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(initiallyOwned: false, AppPaths.SingleInstanceMutex);
        if (!AcquireSingleInstance(e.Args.Contains(RestartArgument)))
        {
            MessageBox.Show(
                "PrintPacer is already running.\n\nLook for its icon in the notification area, " +
                "near the clock.",
                "PrintPacer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // --data-dir=<folder> keeps the job database, settings and logs somewhere else: for trying
        // a build without touching - or resuming - the jobs of the copy that is in real use.
        // It also leaves the user's chosen data folder, and any move they asked for, alone.
        var moved = DataFolderMoveResult.Nothing(DataFolder.DefaultRoot);
        if (e.Args.FirstOrDefault(a => a.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase)) is { } dataDir)
        {
            AppPaths.OverrideRoot(Path.GetFullPath(dataDir["--data-dir=".Length..].Trim('"')));
            DataFolderIsSandbox = true;
        }
        else if (ChooseDataFolder(out moved) is { } root)
        {
            AppPaths.OverrideRoot(root);
        }
        else
        {
            Shutdown();
            return;
        }

        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();
        ThemeManager.Apply(settings.DarkMode);

        LogSetup.Configure(settings.LogRetentionDays, verbose: e.Args.Contains("--verbose"));
        ReportDataFolderMove(moved);

        BindingErrorListener.Attach();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception on a background thread");

        _pendingDocuments = e.Args.Where(a =>
            !a.StartsWith('-') &&
            a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(a)).ToList();

        try
        {
            Start(settingsStore, settings, e.Args.Contains("--minimised") || settings.StartMinimised);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show(
                $"PrintPacer could not start.\n\n{ex.Message}\n\nA log has been written to:\n{AppPaths.LogDirectory}",
                "PrintPacer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>Passed to the copy started by <see cref="Restart"/>, which waits for this one to exit.</summary>
    private const string RestartArgument = "--restarted";

    private bool _ownsSingleInstance;

    private bool AcquireSingleInstance(bool waitForPrevious)
    {
        try
        {
            _ownsSingleInstance = _singleInstance!.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous copy ended without releasing it, which still hands it to us.
            _ownsSingleInstance = true;
        }
        return _ownsSingleInstance;
    }

    /// <summary>
    /// Applies a move the user asked for, then makes sure the data folder can be reached.
    /// Null means the user chose to close PrintPacer. Runs before the log exists; what happened
    /// is logged by <see cref="ReportDataFolderMove"/> once it does.
    /// </summary>
    private static string? ChooseDataFolder(out DataFolderMoveResult moved)
    {
        var choice = new RegistryDataFolderChoice();
        var root = DataFolder.PrepareAtStartup(choice, out moved, installFolder: AppContext.BaseDirectory);

        // A data folder on a drive that is not plugged in. Starting with an empty database
        // somewhere else would make every job seem to have vanished, so ask.
        while (!DataFolder.IsAvailable(root))
        {
            var answer = MessageBox.Show(
                $"PrintPacer keeps its jobs, settings and logs in\n\n    {root}\n\nbut that folder cannot be reached. " +
                "If it is on a drive that is not connected, connect it now.\n\n" +
                "Yes - try again\n" +
                $"No - go back to the standard folder ({DataFolder.DefaultRoot}). Jobs in the other folder are not shown until you switch back in Settings.\n" +
                "Cancel - close PrintPacer",
                "PrintPacer", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);

            if (answer == MessageBoxResult.Cancel) return null;
            if (answer == MessageBoxResult.No)
            {
                choice.Current = null;
                root = DataFolder.DefaultRoot;
            }
        }

        return root;
    }

    private static void ReportDataFolderMove(DataFolderMoveResult moved)
    {
        switch (moved.Outcome)
        {
            case DataFolderMoveOutcome.None:
                return;
            case DataFolderMoveOutcome.Switched:
                Log.Information("Data folder is now {To} (nothing to move from {From})", moved.To, moved.From);
                return;
            case DataFolderMoveOutcome.Moved:
                Log.Information("Moved the data folder from {From} to {To}. {Detail}", moved.From, moved.To, moved.Detail ?? "");
                MessageBox.Show(
                    $"Your jobs, settings and logs are now in\n\n    {moved.To}" +
                    (moved.Detail is null ? "" : $"\n\n{moved.Detail}"),
                    "PrintPacer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            case DataFolderMoveOutcome.UsedExisting:
                Log.Warning("Data folder is now {To}, which already held data; {From} left in place", moved.To, moved.From);
                MessageBox.Show(moved.Detail, "PrintPacer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            case DataFolderMoveOutcome.Failed:
                Log.Error("Could not move the data folder from {From} to {To}: {Detail}", moved.From, moved.To, moved.Detail);
                MessageBox.Show(
                    $"PrintPacer could not move your jobs, settings and logs to\n\n    {moved.To}\n\n{moved.Detail}\n\n" +
                    "You can choose another folder in Settings.",
                    "PrintPacer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
        }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {folder}.\n\n{ex.Message}", "PrintPacer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Starts a fresh copy and exits this one, for a data folder move to take effect. The new
    /// copy waits for this one to release the single-instance mutex before it starts.
    /// </summary>
    internal void Restart()
    {
        if (Environment.ProcessPath is not { } executable) return;

        Log.Information("Restarting to apply a data folder change");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable, RestartArgument) { UseShellExecute = false });
        _exiting = true;
        Shutdown();
    }

    private void Start(SettingsStore settingsStore, AppSettings settings, bool startMinimised)
    {
        var spooler = new WindowsSpooler();
        _store = new JobStore();
        Store = _store;

        _probes = new DeviceProbeFactory(spooler, () => settings);
        _power = new WindowsPowerManager();
        _tray = new TrayNotifier(() => settings.NotificationsEnabled);

        _engine = new JobEngine(
            _store, spooler, new PdfiumPrintEngine(), _probes, settings,
            SystemClock.Instance, _tray, _power, invoicePrinter: new InvoicePrintEngine());

        _engine.RecoveryCompleted += OnRecoveryCompleted;

        _tray.ShowRequested += ShowWindow;
        _tray.ExitRequested += RequestExit;

        // Windows tells us when it is about to suspend and when it comes back. The engine
        // works from absolute times so it does not strictly need this, but logging it makes
        // an overnight run far easier to reason about afterwards.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += (_, _) => Log.Warning("The system clock was changed while a job may be running");
        SystemEvents.SessionEnding += OnSessionEnding;

        _engine.StartAsync().GetAwaiter().GetResult();

        _viewModel = new MainViewModel(_engine, spooler, new PdfService(), settingsStore, _probes, settings);
        _window = new MainWindow(_viewModel);
        _window.CloseRequested += HideToTray;

        // PDFs passed on the command line join the queue as jobs, so "Open with PrintPacer" from
        // File Explorer - for one file or several - lands the user one click from starting.
        if (_pendingDocuments.Count > 0) _viewModel.AddFiles(_pendingDocuments);

        if (!startMinimised)
        {
            _window.Loaded += (_, _) => ShowRecoveryReportIfAny();
            _window.Show();
        }
        else
        {
            Log.Information("Started minimised to the notification area");

            // Nothing to show a dialog over. The job screen carries the same detail, and
            // the notification below is what actually reaches the user.
            if (_recoveryReports is { Count: > 0 } pending)
            {
                var needing = pending.Count(r => r.NeedsUserDecision);
                _tray?.Notify(new Notification(
                    needing > 0 ? NotificationKind.Warning : NotificationKind.Information,
                    needing > 0 ? "A batch needs checking" : "Continuing an unfinished job",
                    needing > 0
                        ? "PrintPacer stopped while pages were in the printer's queue. Open PrintPacer to confirm what printed."
                        : $"{pending[0].FileName}: {pending[0].Summary}"));
            }
        }

        // Keep the tray tooltip in step with what the queue is doing.
        _engine.JobChanged += job => Dispatcher.BeginInvoke(() =>
            _tray?.SetStatus($"PrintPacer — {_viewModel?.QueueHeadline ?? "Idle"}"));
    }

    /// <summary>
    /// Recovery finishes before the main window exists, and it can finish on a background
    /// thread. Rather than racing a queued dispatcher callback against window creation, the
    /// reports are held here and shown once the window is genuinely loaded.
    /// </summary>
    private IReadOnlyList<RecoveryReport>? _recoveryReports;

    private void OnRecoveryCompleted(IReadOnlyList<RecoveryReport> reports)
    {
        if (reports.Count == 0) return;
        _recoveryReports = reports;
    }

    private void ShowRecoveryReportIfAny()
    {
        Log.Debug("Window loaded; pending recovery reports: {Count}", _recoveryReports?.Count ?? 0);

        if (_recoveryReports is not { Count: > 0 } reports) return;
        _recoveryReports = null;

        try
        {
            var window = new RecoveryWindow(reports) { Owner = _window };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            // Failing to show the summary must never stop the user reaching the job screen,
            // which shows the same information per batch anyway.
            Log.Error(ex, "Could not show the recovery summary");
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Log.Information("The computer is suspending. Scheduled batches will resume on wake");
                _store?.Checkpoint();
                break;
            case PowerModes.Resume:
                Log.Information("The computer resumed. Re-checking the schedule");
                _probes?.InvalidateAll();
                break;
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        // Windows is shutting down or the user is logging off. Flush what we know so the
        // next start can recover precisely.
        Log.Information("Session ending ({Reason}); checkpointing job state", e.Reason);
        _store?.Checkpoint();
    }

    private void ShowWindow()
    {
        if (_window is null) return;

        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void HideToTray()
    {
        if (_window is null) return;

        var queueRunning = _viewModel?.HasActiveWork == true;
        _window.Hide();

        if (queueRunning)
        {
            _tray?.Notify(new Notification(NotificationKind.Information,
                "PrintPacer is still running",
                "The print queue continues in the background. Double-click this icon to reopen the window."));
        }
    }

    private void RequestExit()
    {
        if (_exiting) return;

        if (_viewModel?.HasActiveWork == true)
        {
            var answer = MessageBox.Show(
                $"The print queue is still running: {_viewModel.QueueHeadline}.\n\n" +
                "If you exit now, nothing further will be printed until you open PrintPacer again. " +
                "Every job is saved, in order, and will continue when you do.\n\n" +
                "Exit anyway?",
                "PrintPacer", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) return;
        }

        _exiting = true;
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled exception on the UI thread");

        MessageBox.Show(
            $"Something went wrong in the PrintPacer window.\n\n{e.Exception.Message}\n\n" +
            "Any running print job is unaffected and its state has been saved.",
            "PrintPacer", MessageBoxButton.OK, MessageBoxImage.Warning);

        // The UI failing must not take a running job down with it.
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Shutting down");

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;

        _viewModel?.Stop();

        try { _engine?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Debug(ex, "Engine shutdown reported an error"); }

        _probes?.Dispose();
        _power?.Dispose();
        _tray?.Dispose();
        _store?.Dispose();

        LogSetup.Shutdown();

        if (_ownsSingleInstance) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();

        base.OnExit(e);
    }
}
