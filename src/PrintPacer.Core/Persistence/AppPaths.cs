namespace PrintPacer.Core.Persistence;

/// <summary>
/// Every location the application writes to. All of it lives under the per-user
/// application data directory, so the app needs no administrator rights and leaves
/// nothing outside the user's own profile.
/// </summary>
public static class AppPaths
{
    public const string ProductName = "PrintPacer";

    private static string? _rootOverride;

    /// <summary>Redirects all storage, used by the test suite to get an isolated instance.</summary>
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
    public static string DefaultTempDirectory => Path.Combine(Root, "temp");

    /// <summary>Guards against two copies of the application driving the same queue.</summary>
    public const string SingleInstanceMutex = @"Global\PrintPacer.SingleInstance.v1";

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(DefaultTempDirectory);
    }
}
