using PrintPacer.App.Themes;

namespace PrintPacer.App.ViewModels;

/// <summary>Light and dark mode: the header's moon/sun button, and colours chosen in code.</summary>
public sealed partial class MainViewModel
{
    private RelayCommand? _toggleThemeCommand;
    public RelayCommand ToggleThemeCommand => _toggleThemeCommand ??= new RelayCommand(ToggleTheme);

    public bool IsDarkMode => Settings.DarkMode;

    /// <summary>The button shows where it will take you: a sun in dark mode, a moon in light.</summary>
    public string ThemeGlyph => IsDarkMode ? "" : "";

    public string ThemeToolTip => IsDarkMode ? "Switch to light mode" : "Switch to dark mode";

    private void ToggleTheme()
    {
        // Saved straight away, like "Use as defaults": it is a preference, not a job setting.
        Settings.DarkMode = !Settings.DarkMode;
        _settingsStore.Save(Settings);
        ThemeManager.Apply(Settings.DarkMode);
        Raise(nameof(IsDarkMode));
        Raise(nameof(ThemeGlyph));
        Raise(nameof(ThemeToolTip));
    }

    /// <summary>
    /// The views follow a theme change by themselves. Brushes a view model picks by name are
    /// looked up when read, so they are read again.
    /// </summary>
    private void OnThemeChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(ColourModeNoteBrush), nameof(PrinterSetupNoteBrush), nameof(StateForeground),
                     nameof(StateBackground), nameof(PrinterStatusBrush),
                 })
            Raise(name);

        foreach (var item in Queue) item.RefreshBrushes();
        foreach (var batch in Batches) batch.RefreshBrushes();
    }
}
