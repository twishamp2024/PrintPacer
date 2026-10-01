using System.Windows;
using System.Windows.Media;

namespace PrintPacer.App.Views;

/// <summary>
/// The heading of a section: a small tinted icon chip and the section's name. Each section
/// has its own hue, so the page can be scanned by colour as well as by reading. The look
/// is the implicit style in Themes/Styles.xaml.
/// </summary>
public sealed class SectionTitle : System.Windows.Controls.Control
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(SectionTitle), new PropertyMetadata(""));

    /// <summary>A Segoe Fluent Icons / Segoe MDL2 Assets character.</summary>
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SectionTitle), new PropertyMetadata(""));

    public static readonly DependencyProperty HueProperty =
        DependencyProperty.Register(nameof(Hue), typeof(Brush), typeof(SectionTitle), new PropertyMetadata(Brushes.SteelBlue));

    static SectionTitle()
    {
        FocusableProperty.OverrideMetadata(typeof(SectionTitle), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(SectionTitle), new FrameworkPropertyMetadata(false));
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public Brush Hue
    {
        get => (Brush)GetValue(HueProperty);
        set => SetValue(HueProperty, value);
    }
}
