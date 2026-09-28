using FluentAssertions;
using PrintPacer.Core.Pdf;
using PrintPacer.Core.Simulation;
using Xunit;

namespace PrintPacer.Tests;

/// <summary>
/// Adding PDFs: one or many, dropped or picked.
///
/// The window's drop handler passes on the file list Windows hands it
/// (<c>DataFormats.FileDrop</c>, a <c>string[]</c>), and the Browse dialog passes on
/// <c>OpenFileDialog.FileNames</c>, also a <c>string[]</c>. Both go straight to
/// <see cref="PdfIntake"/>, so these tests feed it exactly those arrays. The WPF event plumbing
/// around it is not exercised here.
/// </summary>
public sealed class FileIntakeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "printpacer-tests", Guid.NewGuid().ToString("N"));
    private readonly PdfIntake _intake = new(new PdfService());

    public FileIntakeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* teardown */ }
    }

    private string Pdf(string name, int pages)
    {
        var path = Path.Combine(_root, name);
        TestPdfBuilder.Write(path, pages, Path.GetFileNameWithoutExtension(name));
        return path;
    }

    private string File(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void ASinglePdfIsAccepted()
    {
        var result = _intake.Load(new[] { Pdf("resume.pdf", 6) });

        result.Rejected.Should().BeEmpty();
        result.Accepted.Should().ContainSingle().Which.PageCount.Should().Be(6);
    }

    [Fact]
    public void SeveralPdfsDroppedTogetherAreAllAcceptedInTheOrderGiven()
    {
        // What DataFormats.FileDrop holds when four files are dragged in one go.
        string[] dropped =
        {
            Pdf("assignment.pdf", 84), Pdf("notes.pdf", 21), Pdf("project.pdf", 37), Pdf("resume.pdf", 6),
        };

        var result = _intake.Load(dropped);

        result.Rejected.Should().BeEmpty();
        result.Accepted.Select(d => d.FileName).Should().Equal("assignment.pdf", "notes.pdf", "project.pdf", "resume.pdf");
        result.Accepted.Select(d => d.PageCount).Should().Equal(84, 21, 37, 6);
    }

    [Fact]
    public void SeveralPdfsChosenInTheFilePickerAreAllAccepted()
    {
        // OpenFileDialog.FileNames with Multiselect on. The same file listed twice is one file.
        var rahul = Pdf("Rahul.pdf", 3);
        string[] picked = { rahul, Pdf("Priya.pdf", 5), rahul, Pdf("Amit.pdf", 2) };

        var result = _intake.Load(picked);

        result.Accepted.Select(d => d.FileName).Should().Equal("Rahul.pdf", "Priya.pdf", "Amit.pdf");
        result.Rejected.Should().BeEmpty();
    }

    [Fact]
    public void BadFilesAmongGoodOnesAreEachReportedWithAReasonAndTheGoodOnesAreStillAccepted()
    {
        var folder = Path.Combine(_root, "customer folder");
        Directory.CreateDirectory(folder);

        string[] dropped =
        {
            Pdf("first.pdf", 4),
            File("letter.docx", "PK\u0003\u0004 not a pdf"u8.ToArray()),
            File("broken.pdf", "this was never a PDF"u8.ToArray()),
            File("empty.pdf", Array.Empty<byte>()),
            folder,
            Path.Combine(_root, "deleted meanwhile.pdf"),
            Pdf("second.pdf", 9),
        };

        var result = _intake.Load(dropped);

        result.Accepted.Select(d => d.FileName).Should().Equal("first.pdf", "second.pdf");

        var reasons = result.Rejected.ToDictionary(r => r.FileName, r => r.Reason);
        reasons.Should().HaveCount(5, "no file may be dropped silently");
        reasons["letter.docx"].Should().Contain("Not a PDF").And.Contain("DOCX");
        reasons["broken.pdf"].Should().Contain("not a PDF");
        reasons["empty.pdf"].Should().Contain("empty");
        reasons["customer folder"].Should().Contain("folder");
        reasons["deleted meanwhile.pdf"].Should().Contain("no longer exists");
    }

    [Fact]
    public void APdfWithAnUnusualExtensionIsJudgedByItsContent()
    {
        // The Browse dialog offers "All files", so a PDF saved as .bin still works.
        var path = Pdf("scan.pdf", 2);
        var renamed = Path.ChangeExtension(path, ".bin");
        System.IO.File.Move(path, renamed);

        var result = _intake.Load(new[] { renamed });

        result.Accepted.Should().ContainSingle().Which.PageCount.Should().Be(2);
    }
}
