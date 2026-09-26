using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PrintPacer.App.Platform;
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
    private string? _pendingDocument;

    /// <summary>Exposed for the view model's read-only access to the event log.</summary>
    internal static JobStore? Store { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, AppPaths.SingleInstanceMutex, out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "PrintPacer is already running.\n\nLook for its icon in the notification area, " +
                "near the clock.",
                "PrintPacer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();

        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();

        LogSetup.Configure(settings.LogRetentionDays, verbose: e.Args.Contains("--verbose"));

        BindingErrorListener.Attach();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception on a background thread");

        _pendingDocument = e.Args.FirstOrDefault(a =>
            !a.StartsWith('-') &&
            a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(a));

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

        // A PDF passed on the command line is loaded straight into the setup screen, so
        // "Open with PrintPacer" from File Explorer lands the user one click from starting.
        if (_pendingDocument is { } document) _viewModel.LoadDocument(document);

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

        // Keep the tray tooltip in step with the headline state.
        _engine.JobChanged += job => Dispatcher.BeginInvoke(() =>
            _tray?.SetStatus($"PrintPacer — {_viewModel?.StateHeadline ?? "Idle"}"));
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

        var jobRunning = _viewModel?.CurrentJob is { IsActive: true };
        _window.Hide();

        if (jobRunning)
        {
            _tray?.Notify(new Notification(NotificationKind.Information,
                "PrintPacer is still running",
                "The job continues in the background. Double-click this icon to reopen the window."));
        }
    }

    private void RequestExit()
    {
        if (_exiting) return;

        if (_viewModel?.CurrentJob is { IsActive: true } job)
        {
            var answer = MessageBox.Show(
                $"A print job is still running.\n\n" +
                $"{job.FileName}: {job.PendingBatchCount} batch(es) " +
                $"have not been sent yet.\n\n" +
                "If you exit now, nothing further will be printed until you open PrintPacer again. " +
                "The job is saved and will offer to continue when you do.\n\n" +
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

        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();

        base.OnExit(e);
    }
}
