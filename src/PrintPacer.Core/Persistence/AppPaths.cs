namespace PrintPacer.Core.Persistence;

/// <summary>
/// Every location the application writes to. All of it lives under one data folder: the
/// per-user application data directory unless the user has chosen another
/// (<see cref="DataFolder"/>), which the application sets with <see cref="OverrideRoot"/>
/// at startup.
/// </summary>
public static class AppPaths
{
    public const string ProductName = "PrintPacer";

    private static string? _rootOverride;

    /// <summary>
    /// Redirects all storage: to the user's chosen data folder, to a --data-dir sandbox, or,
    /// in the test suite, to an isolated instance.
    /// </summary>
    public static void OverrideRoot(string root)
    {
        _rootOverride = root;
        Directory.CreateDirectory(root);
    }

    public static string Root => _rootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    public static string DatabasePath => Path.Combine(Root, "jobs.db");
    public static string SettingsPath => Path.Combine(Root, "settings.json");
    public static string LogDirectory => Path.Combine(Root, "logs");

    // No temporary directory: the print engine draws page ranges directly from the source
    // PDF and never writes an intermediate file, so there is nothing to clean up.

    /// <summary>Guards against two copies of the application driving the same queue.</summary>
    public const string SingleInstanceMutex = @"Global\PrintPacer.SingleInstance.v1";

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}
