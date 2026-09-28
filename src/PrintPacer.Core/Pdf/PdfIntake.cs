namespace PrintPacer.Core.Pdf;

/// <summary>A file that could not be added, and why, in words the user can act on.</summary>
public sealed record RejectedFile(string Path, string FileName, string Reason);

/// <summary>What became of a set of files dropped or chosen together.</summary>
public sealed record IntakeResult(IReadOnlyList<PdfDocumentInfo> Accepted, IReadOnlyList<RejectedFile> Rejected);

/// <summary>
/// Turns the files a user dropped or picked - one or many - into documents ready to become
/// jobs, and says exactly why any file was refused.
///
/// Drag-and-drop and the Browse dialog both come through here, so they accept and refuse
/// exactly the same things. One bad file never stops the others: every valid PDF is accepted,
/// every other file is reported with its reason, and the order the files were given in is kept.
/// </summary>
public sealed class PdfIntake
{
    private readonly PdfService _pdf;

    public PdfIntake(PdfService pdf) => _pdf = pdf;

    /// <param name="progress">Told the name of each file as inspection of it begins, with its index.</param>
    public IntakeResult Load(IEnumerable<string> paths, IProgress<(int Index, int Count, string FileName)>? progress = null,
        CancellationToken ct = default)
    {
        var accepted = new List<PdfDocumentInfo>();
        var rejected = new List<RejectedFile>();

        // The same file given twice in one drop is one job, not two.
        var distinct = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Normalise)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < distinct.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var path = distinct[i];
            var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = path;
            progress?.Report((i, distinct.Count, name));

            if (Directory.Exists(path))
            {
                rejected.Add(new RejectedFile(path, name,
                    "This is a folder. Open it and add the PDF files themselves, so nothing is printed by accident."));
                continue;
            }

            if (!File.Exists(path))
            {
                rejected.Add(new RejectedFile(path, name, "The file no longer exists."));
                continue;
            }

            var isPdfName = string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

            try
            {
                accepted.Add(_pdf.Load(path));
            }
            catch (PdfLoadException ex)
            {
                rejected.Add(new RejectedFile(path, name, isPdfName
                    ? ex.Message
                    : $"Not a PDF. Only PDF documents can be printed; this is a {DescribeExtension(path)} file."));
            }
            catch (Exception ex)
            {
                rejected.Add(new RejectedFile(path, name, $"The file could not be opened: {ex.Message}"));
            }
        }

        return new IntakeResult(accepted, rejected);
    }

    private static string Normalise(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    private static string DescribeExtension(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) ? "different kind of" : extension.TrimStart('.').ToUpperInvariant();
    }
}
