using System.Diagnostics;
using System.Globalization;
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

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        KeepAwake.IsChecked = settings.KeepAwakeWhilePrinting;
        AutoResume.IsChecked = settings.AutoResumeUnambiguousJobs;
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
        DataLocation.Text = $"Jobs, settings and logs are stored in {AppPaths.Root}. " +
                            "Nothing is uploaded anywhere, and PDFs are only ever read from where they already are.";
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

        Result = _settings;
        DialogResult = true;
    }

    private bool ReadInt(TextBox box, string label, int min, int max, out int value)
    {
        if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
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
