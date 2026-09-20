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
        return s;
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
