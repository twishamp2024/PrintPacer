using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PrintPacer.Core.Model;
using PrintPacer.Core.Persistence;
using Serilog;

namespace PrintPacer.App.Views;

public partial class SettingsWindow : Window
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PrintPacer";

    private readonly AppSettings _settings;

    public AppSettings? Result { get; private set; }

    public SettingsWindow(AppSettings settings, IReadOnlyList<string> printers)
    {
        InitializeComponent();
        _settings = settings;

        foreach (var printer in printers) DefaultPrinter.Items.Add(printer);
        if (settings.LastPrinterName is { } chosen && !printers.Contains(chosen)) DefaultPrinter.Items.Add(chosen);
        DefaultPrinter.SelectedItem = settings.LastPrinterName ?? printers.FirstOrDefault();
        SelectByTag(DefaultColour, settings.ColourMode.ToString());
        SelectByTag(DefaultSides, settings.DefaultDuplex.ToString());
        DefaultPagesPerBatch.Text = settings.DefaultPagesPerBatch.ToString(CultureInfo.CurrentCulture);
        DefaultDelay.Text = settings.DefaultDelayMinutes.ToString(CultureInfo.CurrentCulture);
        DefaultInvoice.IsChecked = settings.PrintInvoice;
        DefaultBlackWhitePrice.Text = settings.BlackWhitePagePrice.ToString("0.##", CultureInfo.CurrentCulture);
        DefaultColourPrice.Text = settings.ColourPagePrice.ToString("0.##", CultureInfo.CurrentCulture);

        KeepAwake.IsChecked = settings.KeepAwakeWhilePrinting;
        AutoResume.IsChecked = settings.AutoResumeUnambiguousJobs;
        ContinueQueue.IsChecked = settings.ContinueQueueWhenJobNeedsDecision;
        StartWithWindows.IsChecked = settings.LaunchOnWindowsStartup;
        StartMinimised.IsChecked = settings.StartMinimised;

        NotificationsEnabled.IsChecked = settings.NotificationsEnabled;
        NotifyBatch.IsChecked = settings.NotifyOnBatchComplete;
        NotifyProblem.IsChecked = settings.NotifyOnProblem;
        NotifyJob.IsChecked = settings.NotifyOnJobComplete;

        SelectDpi(settings.RenderDpi);

        RetryInterval.Text = settings.RetryIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        BlockedGiveUp.Text = settings.BlockedGiveUpMinutes.ToString(CultureInfo.InvariantCulture);
        StallTimeout.Text = settings.StallTimeoutMinutes.ToString(CultureInfo.InvariantCulture);
        MaxRetries.Text = settings.MaxAutoRetries.ToString(CultureInfo.InvariantCulture);

        DeviceProbeEnabled.IsChecked = settings.DeviceProbeEnabled;
        DeviceAddress.Text = settings.DeviceAddress ?? "";

        LogRetention.Text = settings.LogRetentionDays.ToString(CultureInfo.InvariantCulture);
        DataLocation.Text = "Nothing is uploaded anywhere, and PDFs are only ever read from where they already are.";

        DataFolderPath.Text = AppPaths.Root;
        if (App.DataFolderIsSandbox)
        {
            // A --data-dir run must never move, or point the real install at, its sandbox.
            ChangeDataFolderButton.Visibility = Visibility.Collapsed;
            DataLocation.Text = "This copy was started with --data-dir, so the data folder cannot be changed here. " + DataLocation.Text;
        }
        else
        {
            try { _requestedDataFolder = _dataFolderChoice.Pending; }
            catch (Exception ex) { Log.Warning(ex, "Could not read a pending data folder move"); }
            ShowDataFolderMove();
        }
    }

    // ---- Data folder ----------------------------------------------------------

    private readonly RegistryDataFolderChoice _dataFolderChoice = new();
    private string? _requestedDataFolder;
    private bool _dataFolderChanged;

    /// <summary>True when Save asked for the data to move, which happens when PrintPacer next starts.</summary>
    public bool DataFolderMoveRequested { get; private set; }

    private void ShowDataFolderMove()
    {
        var moving = _requestedDataFolder is not null;
        DataFolderMove.Text = moving
            ? $"Moves to {_requestedDataFolder} the next time PrintPacer starts. Everything is copied and " +
              "checked before the old copy is removed."
            : "";
        DataFolderMove.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
        KeepDataFolderButton.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnChangeDataFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where PrintPacer keeps its jobs, settings and logs",
            InitialDirectory = Path.GetDirectoryName(_requestedDataFolder ?? AppPaths.Root) ?? "",
        };
        if (dialog.ShowDialog(this) != true) return;

        var check = DataFolder.Check(dialog.FolderName, AppPaths.Root, AppContext.BaseDirectory);
        if (!check.IsUsable)
        {
            MessageBox.Show(this, check.Problem, "Data folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (DataFolder.SamePath(check.Folder, AppPaths.Root))
        {
            _requestedDataFolder = null;
        }
        else
        {
            if (check.AddedSubfolder && MessageBox.Show(this,
                    $"{dialog.FolderName} already holds other things, so PrintPacer would keep its data in a folder " +
                    $"of its own inside it:\n\n    {check.Folder}\n\nUse that folder?",
                    "Data folder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            _requestedDataFolder = check.Folder;
        }

        _dataFolderChanged = true;
        ShowDataFolderMove();
    }

    private void OnKeepDataFolder(object sender, RoutedEventArgs e)
    {
        _requestedDataFolder = null;
        _dataFolderChanged = true;
        ShowDataFolderMove();
    }

    /// <summary>Records the move for the next start. Nothing is moved while PrintPacer runs.</summary>
    private void SaveDataFolderChoice()
    {
        if (!_dataFolderChanged) return;
        try
        {
            _dataFolderChoice.Pending = _requestedDataFolder;
            DataFolderMoveRequested = _requestedDataFolder is not null;
            Log.Information("Data folder move {Change}", _requestedDataFolder is null ? "cancelled" : $"requested to {_requestedDataFolder}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not record the data folder move");
        }
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == tag);
        if (box.SelectedItem is null) box.SelectedIndex = 0;
    }

    private void SelectDpi(int dpi)
    {
        foreach (var item in RenderDpi.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && int.TryParse(tag, out var value) && value == dpi)
            {
                RenderDpi.SelectedItem = item;
                return;
            }
        }
        RenderDpi.SelectedIndex = 2;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!ReadInt(RetryInterval, "Re-check interval", 1, 1440, out var retry)) return;
        if (!ReadInt(BlockedGiveUp, "Give-up time", 0, 10080, out var giveUp)) return;
        if (!ReadInt(StallTimeout, "Stall timeout", 2, 1440, out var stall)) return;
        if (!ReadInt(MaxRetries, "Automatic retries", 1, 10, out var retries)) return;
        if (!ReadInt(LogRetention, "Log retention", 1, 365, out var retention)) return;
        if (!ReadInt(DefaultPagesPerBatch, "Batch size", 1, 10_000, out var pagesPerBatch, CultureInfo.CurrentCulture)) return;
        if (!ReadInt(DefaultDelay, "Rest between batches", 0, 24 * 60, out var delay, CultureInfo.CurrentCulture)) return;
        if (!ReadPrice(DefaultBlackWhitePrice, "Black & white page price", out var blackWhitePrice)) return;
        if (!ReadPrice(DefaultColourPrice, "Colour page price", out var colourPrice)) return;

        _settings.LastPrinterName = DefaultPrinter.SelectedItem as string ?? _settings.LastPrinterName;
        _settings.ColourMode = DefaultColour.SelectedItem is ComboBoxItem { Tag: string colour } &&
                               Enum.TryParse<ColourMode>(colour, out var mode) ? mode : ColourMode.Colour;
        _settings.DefaultDuplex = DefaultSides.SelectedItem is ComboBoxItem { Tag: string sides } &&
                                  Enum.TryParse<DuplexMode>(sides, out var duplex) ? duplex : DuplexMode.SingleSided;
        _settings.DefaultPagesPerBatch = pagesPerBatch;
        _settings.DefaultDelayMinutes = delay;
        _settings.PrintInvoice = DefaultInvoice.IsChecked == true;
        _settings.BlackWhitePagePrice = blackWhitePrice;
        _settings.ColourPagePrice = colourPrice;
        _settings.ContinueQueueWhenJobNeedsDecision = ContinueQueue.IsChecked == true;

        _settings.KeepAwakeWhilePrinting = KeepAwake.IsChecked == true;
        _settings.AutoResumeUnambiguousJobs = AutoResume.IsChecked == true;
        _settings.LaunchOnWindowsStartup = StartWithWindows.IsChecked == true;
        _settings.StartMinimised = StartMinimised.IsChecked == true;

        _settings.NotificationsEnabled = NotificationsEnabled.IsChecked == true;
        _settings.NotifyOnBatchComplete = NotifyBatch.IsChecked == true;
        _settings.NotifyOnProblem = NotifyProblem.IsChecked == true;
        _settings.NotifyOnJobComplete = NotifyJob.IsChecked == true;

        if (RenderDpi.SelectedItem is ComboBoxItem { Tag: string dpiTag } && int.TryParse(dpiTag, out var dpi))
            _settings.RenderDpi = dpi;

        _settings.RetryIntervalMinutes = retry;
        _settings.BlockedGiveUpMinutes = giveUp;
        _settings.StallTimeoutMinutes = stall;
        _settings.MaxAutoRetries = retries;

        _settings.DeviceProbeEnabled = DeviceProbeEnabled.IsChecked == true;
        _settings.DeviceAddress = string.IsNullOrWhiteSpace(DeviceAddress.Text) ? null : DeviceAddress.Text.Trim();

        _settings.LogRetentionDays = retention;

        ApplyStartupRegistration(_settings.LaunchOnWindowsStartup, _settings.StartMinimised);
        SaveDataFolderChoice();

        Result = _settings;
        DialogResult = true;
    }

    private bool ReadPrice(TextBox box, string label, out decimal value)
    {
        if ((decimal.TryParse(box.Text, NumberStyles.Currency, CultureInfo.CurrentCulture, out value) ||
             decimal.TryParse(box.Text, NumberStyles.Currency, CultureInfo.InvariantCulture, out value))
            && value is >= 0 and <= 1_000_000m)
        {
            return true;
        }

        ValidationMessage.Text = $"{label} must be a number of 0 or more, for example 2 or 2.50.";
        box.Focus();
        box.SelectAll();
        return false;
    }

    private bool ReadInt(TextBox box, string label, int min, int max, out int value, CultureInfo? culture = null)
    {
        if (int.TryParse(box.Text, NumberStyles.Integer, culture ?? CultureInfo.InvariantCulture, out value)
            && value >= min && value <= max)
        {
            ValidationMessage.Text = "";
            return true;
        }

        ValidationMessage.Text = $"{label} must be a whole number between {min} and {max}.";
        box.Focus();
        box.SelectAll();
        return false;
    }

    /// <summary>
    /// Registers under the per-user Run key. This is the ordinary, unprivileged way to
    /// start with Windows: no scheduled task, no service, no administrator rights, and the
    /// user can see and remove it from Task Manager's Startup tab.
    /// </summary>
    private static void ApplyStartupRegistration(bool enabled, bool minimised)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (!enabled)
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
                return;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable)) return;

            var command = minimised ? $"\"{executable}\" --minimised" : $"\"{executable}\"";
            key.SetValue(RunValue, command, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not update the Windows startup entry");
        }
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open the data folder");
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
