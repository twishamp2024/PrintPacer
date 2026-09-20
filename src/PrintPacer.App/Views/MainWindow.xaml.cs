using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using PrintPacer.App.ViewModels;

namespace PrintPacer.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Brush _dropIdleBorder;
    private readonly Brush _dropIdleBackground;

    /// <summary>Raised when the user closes the window, which hides rather than exits.</summary>
    public event Action? CloseRequested;


    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        _dropIdleBorder = DropTarget.BorderBrush;
        _dropIdleBackground = DropTarget.Background;
    }

    // ---- Drag and drop -----------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var path = SinglePdfFrom(e);
        e.Effects = path is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;

        if (path is not null) HighlightDropZone(true);
    }

    private void OnDragLeave(object sender, DragEventArgs e) => HighlightDropZone(false);

    private void OnDrop(object sender, DragEventArgs e)
    {
        HighlightDropZone(false);

        var path = SinglePdfFrom(e);
        if (path is null) return;

        e.Handled = true;
        _viewModel.LoadDocument(path);
    }

    private void HighlightDropZone(bool active)
    {
        if (DropTarget is null) return;

        DropTarget.BorderBrush = active
            ? (Brush)FindResource("Accent")
            : _dropIdleBorder;

        DropTarget.Background = active
            ? (Brush)FindResource("AccentSoft")
            : _dropIdleBackground;
    }

    /// <summary>
    /// Accepts exactly one PDF. Dropping a folder or a handful of files is far more likely
    /// to be a mistake than a request, so it is refused rather than guessed at.
    /// </summary>
    private static string? SinglePdfFrom(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths) return null;

        var path = paths[0];
        if (!File.Exists(path)) return null;

        return string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }

    // ---- Chrome -------------------------------------------------------------

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_viewModel.Settings.Clone()) { Owner = this };
        if (window.ShowDialog() == true && window.Result is { } updated)
        {
            _viewModel.ApplySettings(updated);
            _viewModel.RefreshPrinters();
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
