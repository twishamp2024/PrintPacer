using PrintPacer.Core.Persistence;
using Serilog;
using Serilog.Events;

namespace PrintPacer.Core.Diagnostics;

/// <summary>
/// Structured file logging.
///
/// One rolling file per day under the per-user application data directory, retained for a
/// configurable number of days. Nothing is sent anywhere: this application has no telemetry
/// and no network dependency beyond talking to the user's own printer.
/// </summary>
public static class LogSetup
{
    public static void Configure(int retentionDays = 30, bool verbose = false)
    {
        AppPaths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(AppPaths.LogDirectory, "printpacer-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Max(1, retentionDays),
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(2),
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("{Product} starting. Data directory: {Root}", AppPaths.ProductName, AppPaths.Root);
    }

    public static void Shutdown() => Log.CloseAndFlush();

    /// <summary>Most recent log file, for the "open logs" action in the UI.</summary>
    public static string? LatestLogFile()
    {
        try
        {
            var directory = new DirectoryInfo(AppPaths.LogDirectory);
            if (!directory.Exists) return null;

            return directory.GetFiles("printpacer-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
