using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using PrintPacer.App.ViewModels;

namespace PrintPacer.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Brush _dropIdleStroke;
    private readonly Brush _dropIdleFill;
    private readonly Brush _queueDropIdleStroke;
    private readonly Brush _queueDropIdleFill;

    /// <summary>Raised when the user closes the window, which hides rather than exits.</summary>
    public event Action? CloseRequested;


    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        _dropIdleStroke = DropTarget.Stroke;
        _dropIdleFill = DropTarget.Fill;
        _queueDropIdleStroke = QueueDropTarget.Stroke;
        _queueDropIdleFill = QueueDropTarget.Fill;
    }

    // ---- Drag and drop -----------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var paths = DroppedFiles(e);
        e.Effects = paths.Count == 0 ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;

        if (paths.Count > 0) HighlightDropZone(true);
    }

    private void OnDragLeave(object sender, DragEventArgs e) => HighlightDropZone(false);

    /// <summary>
    /// Takes every dropped file. Each valid PDF becomes its own job; anything else - a Word
    /// file, a folder, a damaged PDF - is listed in the queue with the reason, never ignored.
    /// </summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        HighlightDropZone(false);

        var paths = DroppedFiles(e);
        if (paths.Count == 0) return;

        e.Handled = true;
        _viewModel.AddFiles(paths);
    }

    private void HighlightDropZone(bool active)
    {
        foreach (var (target, idleStroke, idleFill) in new[]
                 {
                     (DropTarget, _dropIdleStroke, _dropIdleFill),
                     (QueueDropTarget, _queueDropIdleStroke, _queueDropIdleFill),
                 })
        {
            if (target is null) continue;
            target.Stroke = active ? (Brush)FindResource("Accent") : idleStroke;
            target.Fill = active ? (Brush)FindResource("AccentSoft") : idleFill;
        }
    }

    private static IReadOnlyList<string> DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths
            : Array.Empty<string>();

    // ---- Chrome -------------------------------------------------------------

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_viewModel.Settings.Clone(), _viewModel.Printers.ToList()) { Owner = this };
        if (window.ShowDialog() == true && window.Result is { } updated)
        {
            _viewModel.ApplySettings(updated);
            _viewModel.RefreshPrinters();

            if (window.DataFolderMoveRequested) OfferRestartForDataFolderMove();
        }
    }

    /// <summary>
    /// The data moves only while PrintPacer starts, when nothing has the database open. Offer
    /// to restart now, unless the queue is busy: a restart then would interrupt printing.
    /// </summary>
    private void OfferRestartForDataFolderMove()
    {
        if (_viewModel.HasActiveWork)
        {
            MessageBox.Show(this,
                "Your jobs, settings and logs will move the next time PrintPacer starts.\n\n" +
                "The print queue is running, so PrintPacer has not restarted. When it has finished, exit " +
                "PrintPacer from its icon in the notification area and start it again.",
                "Data folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this,
                "Your jobs, settings and logs move the next time PrintPacer starts.\n\nRestart PrintPacer now?",
                "Data folder", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            ((App)Application.Current).Restart();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing hides to the notification area so a running job is never lost to a
        // reflexive click on the X. Exit is an explicit choice from the tray menu.
        e.Cancel = true;
        CloseRequested?.Invoke();
    }
}
