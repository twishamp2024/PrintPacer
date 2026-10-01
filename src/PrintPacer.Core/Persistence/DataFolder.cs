using System.Security.Cryptography;
using Microsoft.Win32;

namespace PrintPacer.Core.Persistence;

/// <summary>
/// The user's choice of data folder: the current one, and one they have asked to move to.
/// Null means the default, <see cref="DataFolder.DefaultRoot"/>.
/// </summary>
public interface IDataFolderChoice
{
    string? Current { get; set; }
    string? Pending { get; set; }
}

/// <summary>
/// Kept in HKCU\Software\PrintPacer, because it has to be found before the data folder is,
/// and because it is per user, like the data itself. The installers write
/// <c>PendingDataFolder</c>; only the application ever moves data and writes
/// <c>DataFolder</c>. Uninstalling leaves the key, as it leaves the data, so a reinstall
/// finds the jobs again.
/// </summary>
public sealed class RegistryDataFolderChoice : IDataFolderChoice
{
    public const string KeyPath = @"Software\PrintPacer";
    private readonly string _keyPath;

    public RegistryDataFolderChoice(string keyPath = KeyPath) => _keyPath = keyPath;

    public string? Current { get => Read("DataFolder"); set => Write("DataFolder", value); }
    public string? Pending { get => Read("PendingDataFolder"); set => Write("PendingDataFolder", value); }

    private string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue(name) is string { Length: > 0 } value ? value : null;
    }

    private void Write(string name, string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        if (string.IsNullOrWhiteSpace(value)) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }
}

public enum DataFolderMoveOutcome
{
    /// <summary>No move was asked for.</summary>
    None,
    /// <summary>There was nothing to move; the new folder is simply used from now on.</summary>
    Switched,
    /// <summary>Everything was copied, checked, and the old copies removed.</summary>
    Moved,
    /// <summary>The new folder already held PrintPacer data, which is used as it is. Nothing was copied or deleted.</summary>
    UsedExisting,
    /// <summary>The move could not be made. The old folder is still used and nothing in it was touched.</summary>
    Failed,
}

public sealed record DataFolderMoveResult(DataFolderMoveOutcome Outcome, string From, string To, string? Detail = null)
{
    public static DataFolderMoveResult Nothing(string root) => new(DataFolderMoveOutcome.None, root, root);
}

/// <summary>What <see cref="DataFolder.Check"/> made of a folder someone picked.</summary>
/// <param name="Folder">The folder to use, which may be a PrintPacer subfolder of the one picked.</param>
/// <param name="Problem">Why it cannot be used, or null.</param>
/// <param name="AddedSubfolder">True when <paramref name="Folder"/> is a subfolder of the one picked, which the user should be told about.</param>
public sealed record DataFolderCheck(string Folder, string? Problem, bool AddedSubfolder)
{
    public bool IsUsable => Problem is null;
}

/// <summary>
/// Where the job database, settings and logs live, and moving them somewhere else.
///
/// A move happens only at startup, before anything opens the database or a log file, so
/// nothing can be writing while it runs. It copies, then checks every copy byte for byte,
/// then switches, and only then deletes the originals. Any failure before the switch
/// removes the partial copy and leaves the old folder exactly as it was, still in use.
/// The job database is the one thing that must never be lost: it is what stops a batch
/// being printed twice.
/// </summary>
public static class DataFolder
{
    /// <summary>Files whose presence means a folder already holds PrintPacer data.</summary>
    private static readonly string[] DataMarkers = { "jobs.db", "settings.json" };

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.ProductName);

    /// <summary>The folder in use now, without moving anything.</summary>
    public static string CurrentRoot(IDataFolderChoice choice, string? defaultRoot = null) =>
        choice.Current is { } current ? Normalise(current) : defaultRoot ?? DefaultRoot;

    /// <summary>
    /// Applies a pending move, if there is one, and returns the folder to use. Never throws:
    /// a move that fails leaves the old folder in use and says why in the result.
    /// </summary>
    /// <param name="installFolder">The program folder, which the data must stay out of.</param>
    /// <param name="defaultRoot">Stands in for <see cref="DefaultRoot"/>, so tests never touch the real data.</param>
    public static string PrepareAtStartup(IDataFolderChoice choice, out DataFolderMoveResult result,
        string? installFolder = null, string? defaultRoot = null)
    {
        defaultRoot ??= DefaultRoot;
        var from = CurrentRoot(choice, defaultRoot);
        result = DataFolderMoveResult.Nothing(from);

        var pending = choice.Pending;
        if (pending is null) return from;

        string to;
        try { to = Normalise(pending); }
        catch (Exception ex)
        {
            choice.Pending = null;
            result = new DataFolderMoveResult(DataFolderMoveOutcome.Failed, from, pending, $"'{pending}' is not a valid folder: {ex.Message}");
            return from;
        }

        // The installers check too, but cannot see everything (a mapped network drive, or
        // what the user, rather than an elevated setup, may write to). Checked again here,
        // on exactly the folder asked for.
        result = !SamePath(from, to) && ProblemWith(to, from, installFolder) is { } problem
            ? new DataFolderMoveResult(DataFolderMoveOutcome.Failed, from, to, $"{problem} Your data is still in {from}, untouched.")
            : Move(from, to);

        // Recorded whatever happened, so a move that cannot be made is not retried at
        // every start. The user is told, and can ask again.
        choice.Pending = null;
        if (result.Outcome is DataFolderMoveOutcome.Moved or DataFolderMoveOutcome.Switched or DataFolderMoveOutcome.UsedExisting)
        {
            choice.Current = SamePath(to, defaultRoot) ? null : to;
            return to;
        }
        return from;
    }

    /// <summary>
    /// Checks a folder picked for the data. A drive root or a folder holding other things is
    /// given a PrintPacer subfolder, since the folder should be PrintPacer's own.
    /// </summary>
    public static DataFolderCheck Check(string picked, string? currentRoot = null, string? installFolder = null)
    {
        string folder;
        try { folder = Normalise(picked); }
        catch (Exception ex) { return new DataFolderCheck(picked, $"That is not a valid folder: {ex.Message}", false); }

        if (folder.StartsWith(@"\\", StringComparison.Ordinal))
            return new DataFolderCheck(folder, ProblemWith(folder, currentRoot, installFolder), false);

        var addedSubfolder = false;
        if (IsDriveRoot(folder) || (Directory.Exists(folder) && !HoldsPrintPacerData(folder) && !IsEmpty(folder)))
        {
            folder = Path.Combine(folder, AppPaths.ProductName);
            addedSubfolder = true;
        }

        return new DataFolderCheck(folder, ProblemWith(folder, currentRoot, installFolder), addedSubfolder);
    }

    /// <summary>Why the data cannot go in exactly this folder, or null when it can.</summary>
    private static string? ProblemWith(string folder, string? currentRoot, string? installFolder)
    {
        if (folder.StartsWith(@"\\", StringComparison.Ordinal) || DriveTypeOf(folder) == DriveType.Network)
            return "The data folder has to be on this computer. The job database cannot be kept safely on a network drive.";

        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 })
        {
            var path = Environment.GetFolderPath(special);
            if (path.Length > 0 && IsWithin(folder, path))
                return $"PrintPacer cannot keep its data in {path}: Windows does not let it write there without administrator rights.";
        }

        if (installFolder is not null && (IsWithin(folder, installFolder) || IsWithin(installFolder, folder)))
            return "Please keep the data apart from the program folder, so that updating or removing the program can never touch your jobs.";

        if (currentRoot is not null && !SamePath(folder, currentRoot) &&
            (IsWithin(folder, currentRoot) || IsWithin(currentRoot, folder)))
            return "The new folder cannot be inside the current one, or the other way round.";

        if (!CanWriteTo(folder))
            return $"PrintPacer cannot write to {folder}. The drive may be read-only, full or disconnected, or you may not have permission.";

        return null;
    }

    /// <summary>True when the folder can be used right now: its drive is there and it can be created.</summary>
    public static bool IsAvailable(string root)
    {
        try
        {
            var drive = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive)) return false;
            Directory.CreateDirectory(root);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static DataFolderMoveResult Move(string from, string to)
    {
        if (SamePath(from, to))
            return new DataFolderMoveResult(DataFolderMoveOutcome.Switched, from, to);

        if (IsWithin(to, from) || IsWithin(from, to))
            return new DataFolderMoveResult(DataFolderMoveOutcome.Failed, from, to,
                "The new folder cannot be inside the current one, or the other way round.");

        var sources = Directory.Exists(from)
            ? Directory.GetFiles(from, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();

        if (sources.Length == 0)
        {
            return CanWriteTo(to)
                ? new DataFolderMoveResult(DataFolderMoveOutcome.Switched, from, to)
                : new DataFolderMoveResult(DataFolderMoveOutcome.Failed, from, to, $"PrintPacer cannot write to {to}.");
        }

        if (HoldsPrintPacerData(to))
            return new DataFolderMoveResult(DataFolderMoveOutcome.UsedExisting, from, to,
                $"{to} already held PrintPacer data, so that is used as it is. Nothing was copied over it, " +
                $"and the data in {from} was left where it is.");

        var created = new List<string>();
        try
        {
            Directory.CreateDirectory(to);
            foreach (var source in sources)
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, source));
                if (File.Exists(target))
                    throw new IOException($"{target} already exists, and PrintPacer will not copy over it.");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
                created.Add(target);

                if (!SameContent(source, target))
                    throw new IOException($"The copy of {Path.GetFileName(source)} did not match the original.");
            }
        }
        catch (Exception ex)
        {
            Undo(created, to);
            return new DataFolderMoveResult(DataFolderMoveOutcome.Failed, from, to,
                $"{ex.Message} Your data is still in {from}, untouched.");
        }

        // Every file is safely in its new place. The old copies are removed from here on,
        // and a failure to remove one costs only disk space.
        var leftBehind = 0;
        foreach (var source in sources)
        {
            try { File.Delete(source); }
            catch (Exception) { leftBehind++; }
        }
        RemoveEmptyFolders(from);

        return new DataFolderMoveResult(DataFolderMoveOutcome.Moved, from, to,
            leftBehind == 0 ? null : $"{leftBehind} old file(s) in {from} could not be removed; they can be deleted by hand.");
    }

    public static bool HoldsPrintPacerData(string folder) =>
        DataMarkers.Any(name => File.Exists(Path.Combine(folder, name)));

    public static string Normalise(string path)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        return IsDriveRoot(full) ? full : full.TrimEnd(Path.DirectorySeparatorChar);
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="child"/> is <paramref name="parent"/> itself or anywhere beneath it.</summary>
    public static bool IsWithin(string child, string parent) =>
        (Normalise(child).TrimEnd('\\') + '\\').StartsWith(Normalise(parent).TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);

    private static bool IsDriveRoot(string path) =>
        string.Equals(Path.GetPathRoot(path)?.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsEmpty(string folder) => !Directory.EnumerateFileSystemEntries(folder).Any();

    private static DriveType DriveTypeOf(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).DriveType; }
        catch (Exception) { return DriveType.Unknown; }
    }

    /// <summary>Tested on the folder, or on the nearest folder above it that exists, without creating anything.</summary>
    private static bool CanWriteTo(string folder)
    {
        var existing = folder;
        while (!Directory.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing);
            if (existing is null) return false;
        }

        var probe = Path.Combine(existing, $".printpacer-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var first = File.OpenRead(a);
        using var second = File.OpenRead(b);
        return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }

    private static void Undo(List<string> created, string to)
    {
        foreach (var file in created)
        {
            try { File.Delete(file); } catch (Exception) { /* best effort; the originals are intact */ }
        }
        RemoveEmptyFolders(to);
    }

    /// <summary>Removes empty folders beneath and including <paramref name="root"/>, deepest first.</summary>
    private static void RemoveEmptyFolders(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var folder in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(f => f.Length))
            {
                if (IsEmpty(folder)) Directory.Delete(folder);
            }
            if (IsEmpty(root)) Directory.Delete(root);
        }
        catch (Exception)
        {
            // An empty folder left behind is harmless.
        }
    }
}
