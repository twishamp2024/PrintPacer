using FluentAssertions;
using PrintPacer.Core.Model;
using PrintPacer.Core.Persistence;
using Xunit;

namespace PrintPacer.Tests;

/// <summary>
/// Choosing where the jobs, settings and logs live, and moving them there.
///
/// Every test passes its own default folder to <see cref="DataFolder.PrepareAtStartup"/>, and
/// keeps the choice in memory rather than the registry, so nothing here can reach the real
/// %LOCALAPPDATA%\PrintPacer or the real HKCU\Software\PrintPacer.
/// </summary>
public sealed class DataFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "printpacer-tests", Guid.NewGuid().ToString("N"));
    private readonly string _default;

    public DataFolderTests()
    {
        Directory.CreateDirectory(_root);
        _default = Path.Combine(_root, "LocalAppData", "PrintPacer");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* teardown */ }
    }

    private sealed class Choice : IDataFolderChoice
    {
        public string? Current { get; set; }
        public string? Pending { get; set; }
    }

    private string Folder(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    /// <summary>A real data folder: a job database with two jobs, settings, and a log.</summary>
    private void WriteData(string folder)
    {
        using (var store = new JobStore(Path.Combine(folder, "jobs.db")))
        {
            store.InsertJob(Job("first"));
            store.InsertJob(Job("second"));
        }
        new SettingsStore(Path.Combine(folder, "settings.json")).Save(new AppSettings { DefaultPagesPerBatch = 37 });
        Directory.CreateDirectory(Path.Combine(folder, "logs"));
        File.WriteAllText(Path.Combine(folder, "logs", "printpacer-20261001.log"), "a log line");
    }

    private static void ShouldHoldTheData(string folder)
    {
        using (var store = new JobStore(Path.Combine(folder, "jobs.db")))
            store.LoadAllJobs().Select(j => j.Id).Should().BeEquivalentTo("first", "second");
        new SettingsStore(Path.Combine(folder, "settings.json")).Load().DefaultPagesPerBatch.Should().Be(37);
        File.ReadAllText(Path.Combine(folder, "logs", "printpacer-20261001.log")).Should().Be("a log line");
    }

    // ---- Moving at startup ---------------------------------------------------------------

    [Fact]
    public void WithNothingPendingTheCurrentFolderIsUsedAndNothingMoves()
    {
        WriteData(_default);
        var choice = new Choice();

        DataFolder.PrepareAtStartup(choice, out var result, defaultRoot: _default).Should().Be(_default);

        result.Outcome.Should().Be(DataFolderMoveOutcome.None);
        ShouldHoldTheData(_default);
    }

    [Fact]
    public void APendingMoveCopiesEverythingChecksItAndRemovesTheOldCopy()
    {
        WriteData(_default);
        var target = Folder("D", "PrintPacer data");
        var choice = new Choice { Pending = target };

        var root = DataFolder.PrepareAtStartup(choice, out var result, defaultRoot: _default);

        root.Should().Be(target);
        result.Outcome.Should().Be(DataFolderMoveOutcome.Moved);
        result.Detail.Should().BeNull();
        ShouldHoldTheData(target);
        Directory.Exists(_default).Should().BeFalse("the old folder is removed once it is empty");
        choice.Current.Should().Be(target);
        choice.Pending.Should().BeNull();
    }

    [Fact]
    public void TheMoveKeepsTheJobDatabaseIntactWhenItsWriteAheadLogIsStillSeparate()
    {
        // A crash can leave committed changes in jobs.db-wal rather than jobs.db. Moving the
        // two files together keeps those changes.
        Directory.CreateDirectory(_default);
        var store = new JobStore(Path.Combine(_default, "jobs.db"));
        store.InsertJob(Job("first"));
        store.InsertJob(Job("second"));
        foreach (var file in Directory.GetFiles(_default))
            File.Copy(file, Path.Combine(_root, "snapshot-" + Path.GetFileName(file)));
        store.Dispose();

        // Recreate the uncheckpointed state from the snapshot taken while the store was open.
        foreach (var file in Directory.GetFiles(_default)) File.Delete(file);
        foreach (var file in Directory.GetFiles(_root, "snapshot-*"))
            File.Copy(file, Path.Combine(_default, Path.GetFileName(file)["snapshot-".Length..]));
        File.Exists(Path.Combine(_default, "jobs.db-wal")).Should().BeTrue("the test needs a separate WAL");

        var target = Folder("Moved");
        DataFolder.PrepareAtStartup(new Choice { Pending = target }, out var result, defaultRoot: _default);

        result.Outcome.Should().Be(DataFolderMoveOutcome.Moved);
        using var moved = new JobStore(Path.Combine(target, "jobs.db"));
        moved.LoadAllJobs().Select(j => j.Id).Should().BeEquivalentTo("first", "second");
    }

    [Fact]
    public void MovingBackToTheDefaultFolderForgetsTheChoice()
    {
        var elsewhere = Folder("Elsewhere");
        WriteData(elsewhere);
        var choice = new Choice { Current = elsewhere, Pending = _default };

        DataFolder.PrepareAtStartup(choice, out var result, defaultRoot: _default).Should().Be(_default);

        result.Outcome.Should().Be(DataFolderMoveOutcome.Moved);
        ShouldHoldTheData(_default);
        choice.Current.Should().BeNull("the default needs no setting");
    }

    [Fact]
    public void WithNothingToMoveTheNewFolderIsSimplyUsed()
    {
        var target = Folder("Fresh");
        var choice = new Choice { Pending = target };

        DataFolder.PrepareAtStartup(choice, out var result, defaultRoot: _default).Should().Be(target);

        result.Outcome.Should().Be(DataFolderMoveOutcome.Switched);
        choice.Current.Should().Be(target);
    }

    [Fact]
    public void AFolderThatAlreadyHoldsDataIsUsedAsItIsAndNothingIsOverwrittenOrDeleted()
    {
        WriteData(_default);
        var existing = Folder("Existing");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "settings.json"), "{ }");
        var choice = new Choice { Pending = existing };

        DataFolder.PrepareAtStartup(choice, out var result, defaultRoot: _default).Should().Be(existing);

        result.Outcome.Should().Be(DataFolderMoveOutcome.UsedExisting);
        File.ReadAllText(Path.Combine(existing, "settings.json")).Should().Be("{ }");
        File.Exists(Path.Combine(existing, "jobs.db")).Should().BeFalse();
        ShouldHoldTheData(_default);
        choice.Current.Should().Be(existing);
    }

    [Fact]
    public void AFailedMoveLeavesTheOldFolderInUseAndUntouchedAndRemovesThePartialCopy()
    {
        WriteData(_default);
        var target = Folder("Target");
        Directory.CreateDirectory(Path.Combine(target, "logs"));
        // A file in the way, met part-way through the copy.
        File.WriteAllText(Path.Combine(target, "logs", "printpacer-20261001.log"), "someone else's");
        var before = Directory.GetFiles(_default, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllBytes);
        var choice = new Choice { Current = _default, Pending = target };

        DataFolder.PrepareAtStartup(choice, out var result, defaultRoot: _default).Should().Be(_default);

        result.Outcome.Should().Be(DataFolderMoveOutcome.Failed);
        result.Detail.Should().Contain("untouched");
        foreach (var (file, bytes) in before) File.ReadAllBytes(file).Should().Equal(bytes);
        Directory.GetFiles(target, "*", SearchOption.AllDirectories)
            .Should().ContainSingle().Which.Should().EndWith("printpacer-20261001.log");
        File.ReadAllText(Path.Combine(target, "logs", "printpacer-20261001.log")).Should().Be("someone else's");
        choice.Current.Should().Be(_default);
        choice.Pending.Should().BeNull("a failed move is reported once, not retried at every start");
    }

    [Fact]
    public void AMoveIntoItsOwnSubfolderIsRefused()
    {
        WriteData(_default);
        var choice = new Choice { Pending = Path.Combine(_default, "inner") };

        DataFolder.PrepareAtStartup(choice, out var result, defaultRoot: _default).Should().Be(_default);

        result.Outcome.Should().Be(DataFolderMoveOutcome.Failed);
        ShouldHoldTheData(_default);
    }

    [Fact]
    public void APendingMoveIsCheckedAgainAtStartupAndAnUnusableFolderIsRefused()
    {
        // An all-users setup runs elevated, so it cannot tell whether the user can write
        // somewhere. PrintPacer checks again, as the user, before moving anything.
        WriteData(_default);
        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PrintPacer data");
        var choice = new Choice { Pending = programFiles };

        DataFolder.PrepareAtStartup(choice, out var result, defaultRoot: _default).Should().Be(_default);

        result.Outcome.Should().Be(DataFolderMoveOutcome.Failed);
        result.Detail.Should().Contain("administrator").And.Contain("untouched");
        ShouldHoldTheData(_default);
        Directory.Exists(programFiles).Should().BeFalse();
    }

    [Fact]
    public void APendingMoveIntoTheProgramFolderIsRefused()
    {
        WriteData(_default);
        var program = Folder("Program");
        Directory.CreateDirectory(program);
        var choice = new Choice { Pending = Path.Combine(program, "data") };

        DataFolder.PrepareAtStartup(choice, out var result, installFolder: program, defaultRoot: _default)
            .Should().Be(_default);

        result.Outcome.Should().Be(DataFolderMoveOutcome.Failed);
        ShouldHoldTheData(_default);
    }

    [Fact]
    public void AMoveToTheSameFolderChangesNothing()
    {
        WriteData(_default);
        var choice = new Choice { Pending = _default + "\\" };

        DataFolder.PrepareAtStartup(choice, out var result, defaultRoot: _default).Should().Be(_default);

        result.Outcome.Should().Be(DataFolderMoveOutcome.Switched);
        ShouldHoldTheData(_default);
        choice.Current.Should().BeNull();
    }

    // ---- Checking a picked folder ---------------------------------------------------------

    [Fact]
    public void AnEmptyOrNewFolderIsUsedAsPicked()
    {
        var empty = Folder("Empty");
        Directory.CreateDirectory(empty);

        DataFolder.Check(empty).Should().Be(new DataFolderCheck(empty, null, false));
        DataFolder.Check(Folder("New", "Data")).IsUsable.Should().BeTrue();
    }

    [Fact]
    public void AFolderHoldingOtherThingsGetsAPrintPacerSubfolder()
    {
        var documents = Folder("Documents");
        Directory.CreateDirectory(documents);
        File.WriteAllText(Path.Combine(documents, "letter.txt"), "");

        var check = DataFolder.Check(documents);

        check.IsUsable.Should().BeTrue();
        check.AddedSubfolder.Should().BeTrue();
        check.Folder.Should().Be(Path.Combine(documents, "PrintPacer"));
    }

    [Fact]
    public void AFolderAlreadyHoldingPrintPacerDataIsUsedAsPicked()
    {
        var existing = Folder("Existing");
        WriteData(existing);

        DataFolder.Check(existing).Should().Be(new DataFolderCheck(existing, null, false));
    }

    [Fact]
    public void ADriveRootGetsAPrintPacerSubfolder()
    {
        var drive = Path.GetPathRoot(_root)!;

        var check = DataFolder.Check(drive);

        check.Folder.Should().Be(Path.Combine(drive, "PrintPacer"));
        check.AddedSubfolder.Should().BeTrue();
    }

    [Fact]
    public void FoldersPrintPacerCannotWriteToAsAStandardUserAreRefused()
    {
        DataFolder.Check(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PrintPacer data"))
            .Problem.Should().Contain("administrator");
        DataFolder.Check(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "PrintPacer"))
            .Problem.Should().Contain("administrator");
    }

    [Fact]
    public void NetworkFoldersAreRefused()
    {
        DataFolder.Check(@"\\server\share\PrintPacer").Problem.Should().Contain("network");
    }

    [Fact]
    public void TheDataIsKeptApartFromTheProgramFolder()
    {
        var program = Folder("Program");
        Directory.CreateDirectory(program);

        DataFolder.Check(Path.Combine(program, "data"), installFolder: program).Problem.Should().Contain("program folder");
        DataFolder.Check(_root, installFolder: program).IsUsable.Should().BeTrue("a non-empty parent gets its own subfolder");
    }

    [Fact]
    public void AFolderInsideTheCurrentOneIsRefused()
    {
        WriteData(_default);

        DataFolder.Check(Path.Combine(_default, "inner"), currentRoot: _default).Problem.Should().Contain("inside");
        DataFolder.Check(_default, currentRoot: _default).IsUsable.Should().BeTrue("choosing the current folder is no change");
    }

    private static PrintJob Job(string id)
    {
        var job = new PrintJob
        {
            Id = id,
            SourcePath = "C:\\nowhere\\book.pdf",
            FileName = "book.pdf",
            FileSha256 = "00",
            TotalPages = 10,
            PrinterName = "Printer",
            PagesPerBatch = 5,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        job.Batches.AddRange(PrintJob.PlanBatches(id, 10, 5));
        return job;
    }
}
