using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PrintPacer.App.Themes;

/// <summary>
/// Switches between the light and dark colours. The palette lives in Colors.Light.xaml and
/// Colors.Dark.xaml, one of which is merged into the application's resources; every view
/// refers to its brushes with DynamicResource, so replacing the dictionary recolours open
/// windows at once. Each window's title bar is asked to follow, so a dark window does not
/// sit under a white caption.
/// </summary>
public static class ThemeManager
{
    private const string PaletteFolder = "Themes/Colors.";

    static ThemeManager()
    {
        // Windows opened later (Settings, the recovery report) pick up the title bar too.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ApplyTitleBar((Window)sender)));
    }

    public static bool IsDark { get; private set; }

    /// <summary>Raised after the colours change, for code that reads brushes itself.</summary>
    public static event Action? Changed;

    public static void Apply(bool dark)
    {
        if (Application.Current is not { } app) return;

        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/PrintPacer;component/Themes/Colors.{(dark ? "Dark" : "Light")}.xaml"),
        };

        var merged = app.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains(PaletteFolder) == true);
        if (existing is null) merged.Insert(0, palette);
        else merged[merged.IndexOf(existing)] = palette;

        IsDark = dark;
        foreach (Window window in app.Windows) ApplyTitleBar(window);
        Changed?.Invoke();
    }

    private static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        // Attribute 20 on Windows 10 20H1 and later, 19 before it. Older systems ignore both,
        // which only leaves the caption light.
        var value = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
