using System.Xml.Linq;
using FluentAssertions;
using PrintPacer.Core.Model;
using PrintPacer.Core.Persistence;
using Xunit;

namespace PrintPacer.Tests;

/// <summary>
/// Light and dark mode. The two palettes are swapped at run time, so a key missing from one
/// of them would leave part of a window in the other theme's colour, or blank.
/// </summary>
public class ThemeTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheDarkPaletteDefinesExactlyTheKeysOfTheLightOne()
    {
        var light = Keys("Colors.Light.xaml");
        var dark = Keys("Colors.Dark.xaml");

        light.Should().NotBeEmpty();
        dark.Should().BeEquivalentTo(light);
    }

    [Fact]
    public void EveryColourAViewAsksForIsInThePalette()
    {
        var palette = Keys("Colors.Light.xaml");
        var views = Directory.GetFiles(Path.Combine(AppFolder(), "Views"), "*.xaml")
            .Append(Path.Combine(AppFolder(), "Themes", "Styles.xaml"));

        foreach (var view in views)
        {
            var text = File.ReadAllText(view);
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(text, @"\{DynamicResource (\w+)\}"))
            {
                palette.Should().Contain(match.Groups[1].Value, $"{Path.GetFileName(view)} refers to it");
            }

            // A palette brush fixed with StaticResource would not change with the theme.
            foreach (var key in palette)
                text.Should().NotContain($"{{StaticResource {key}}}", $"{Path.GetFileName(view)} must follow the theme");
        }
    }

    [Fact]
    public void DarkModeIsOffByDefaultAndRemembered()
    {
        var folder = Path.Combine(Path.GetTempPath(), "printpacer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var store = new SettingsStore(Path.Combine(folder, "settings.json"));

        store.Load().DarkMode.Should().BeFalse("no settings file yet");

        store.Save(new AppSettings { DarkMode = true });
        store.Load().DarkMode.Should().BeTrue();
        store.Load().Clone().DarkMode.Should().BeTrue("the Settings window works on a copy");
    }

    private static IReadOnlyCollection<string> Keys(string file) =>
        XDocument.Load(Path.Combine(AppFolder(), "Themes", file)).Root!
            .Elements()
            .Select(e => (string?)e.Attribute(Xaml + "Key"))
            .OfType<string>()
            .ToList();

    private static string AppFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PrintPacer.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("PrintPacer.sln not found"),
            "src", "PrintPacer.App");
    }
}
