using System.Text.Json;
using System.Text.Json.Serialization;
using PrintPacer.Core.Model;
using Serilog;

namespace PrintPacer.Core.Persistence;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON, separately from the job database.
///
/// Kept separate deliberately: settings are edited by hand often enough that a malformed
/// file is a realistic event, and a malformed settings file must never be able to prevent
/// an in-progress print job from being recovered. A bad file is backed up and replaced with
/// defaults rather than throwing.
/// </summary>
public sealed class SettingsStore
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SettingsStore>();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Enums as names, so settings.json stays readable and editable by hand.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string? path = null) => _path = path ?? AppPaths.SettingsPath;

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new AppSettings();

            try
            {
                var json = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings is not null) return Sanitise(settings);

                Log.Warning("Settings file was empty; using defaults");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Settings file at {Path} could not be read; using defaults", _path);
                TryBackupCorruptFile();
            }

            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // Write to a temporary file and swap it in, so an interrupted save cannot
                // leave a half-written settings file behind.
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(Sanitise(settings), Options));

                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not save settings to {Path}", _path);
            }
        }
    }

    /// <summary>Clamps values that would make the engine behave badly if edited by hand.</summary>
    private static AppSettings Sanitise(AppSettings s)
    {
        s.DefaultPagesPerBatch = Math.Clamp(s.DefaultPagesPerBatch, 1, 10_000);
        s.DefaultDelayMinutes = Math.Clamp(s.DefaultDelayMinutes, 0, 24 * 60);
        s.RenderDpi = Math.Clamp(s.RenderDpi, 96, 600);
        // Zero is meaningful: poll the queue on every tick.
        s.SpoolerPollSeconds = Math.Clamp(s.SpoolerPollSeconds, 0, 60);
        s.DevicePollSeconds = Math.Clamp(s.DevicePollSeconds, 5, 600);
        s.RetryIntervalMinutes = Math.Clamp(s.RetryIntervalMinutes, 1, 24 * 60);
        s.MaxAutoRetries = Math.Clamp(s.MaxAutoRetries, 1, 10);
        s.BlockedGiveUpMinutes = Math.Clamp(s.BlockedGiveUpMinutes, 0, 7 * 24 * 60);
        s.StallTimeoutMinutes = Math.Clamp(s.StallTimeoutMinutes, 2, 24 * 60);
        s.LogRetentionDays = Math.Clamp(s.LogRetentionDays, 1, 365);
        s.BlackWhitePagePrice = Math.Clamp(s.BlackWhitePagePrice, 0m, 1_000_000m);
        s.ColourPagePrice = Math.Clamp(s.ColourPagePrice, 0m, 1_000_000m);

        if (s.LegacyGrayscale == true) s.ColourMode = ColourMode.BlackAndWhite;
        s.LegacyGrayscale = null;
        if (!Enum.IsDefined(s.ColourMode)) s.ColourMode = ColourMode.Colour;
        if (!Enum.IsDefined(s.DefaultDuplex)) s.DefaultDuplex = DuplexMode.SingleSided;

        s.LayoutDefaults = SanitiseLayout(s.LayoutDefaults);

        return s;
    }

    private static PrintSettings SanitiseLayout(PrintSettings? layout)
    {
        var d = PrintSettings.Default;
        if (layout is null) return d;

        return d with
        {
            Paper = Enum.IsDefined(layout.Paper) ? layout.Paper : d.Paper,
            MarginMm = double.IsFinite(layout.MarginMm) ? Math.Clamp(layout.MarginMm, 0, PrintSettings.MaxMarginMm) : d.MarginMm,
            FoldGapMm = double.IsFinite(layout.FoldGapMm) ? Math.Clamp(layout.FoldGapMm, 0, 2 * PrintSettings.MaxMarginMm) : d.FoldGapMm,
            Scaling = Enum.IsDefined(layout.Scaling) ? layout.Scaling : d.Scaling,
            Binding = Enum.IsDefined(layout.Binding) ? layout.Binding : d.Binding,
            SheetsPerSignature = Math.Clamp(layout.SheetsPerSignature, 0, PrintSettings.MaxSheetsPerSignature),
            KeepLastPageAsBackCover = layout.KeepLastPageAsBackCover,
            Fold = Enum.IsDefined(layout.Fold) ? layout.Fold : d.Fold,
            // Page selection, layout and sides are chosen afresh for every job.
        };
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            var backup = _path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(_path, backup, overwrite: true);
            Log.Information("Kept a copy of the unreadable settings file at {Backup}", backup);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not back up the corrupt settings file");
        }
    }
}
