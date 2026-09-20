using System.Security.Cryptography;
using Docnet.Core;
using Docnet.Core.Models;
using PdfSharp.Pdf.IO;
using Serilog;

namespace PrintPacer.Core.Pdf;

/// <summary>
/// Inspects PDFs.
///
/// Page count comes from PDFium (via Docnet), because PDFium is the engine that will
/// actually rasterise the pages: asking a second library how many pages there are would
/// risk the counts disagreeing, and a disagreement about page count silently corrupts
/// every batch boundary.
///
/// Metadata comes from PDFsharp, which exposes the document information dictionary that
/// PDFium does not surface. Metadata failure is never fatal.
/// </summary>
public sealed class PdfService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PdfService>();

    /// <summary>
    /// PDFium is not thread-safe. Every call into Docnet in this process funnels through
    /// this lock, including the rendering done by the print engine.
    /// </summary>
    internal static readonly object PdfiumLock = new();

    public PdfDocumentInfo Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new PdfLoadException("No file was supplied.");

        var file = new FileInfo(path);
        if (!file.Exists)
            throw new PdfLoadException($"The file does not exist:\n{path}");
        if (file.Length == 0)
            throw new PdfLoadException("The file is empty (0 bytes).");

        if (!LooksLikePdf(path, out var headerProblem))
            throw new PdfLoadException(headerProblem!);

        var warnings = new List<string>();

        int pageCount;
        double? firstWidth = null, firstHeight = null;
        try
        {
            lock (PdfiumLock)
            {
                using var reader = DocLib.Instance.GetDocReader(path, new PageDimensions(1.0));
                pageCount = reader.GetPageCount();
                if (pageCount > 0)
                {
                    using var page = reader.GetPageReader(0);
                    // At a scaling factor of 1.0 the reported size is in PDF points.
                    firstWidth = page.GetPageWidth();
                    firstHeight = page.GetPageHeight();
                }
            }
        }
        catch (Exception ex)
        {
            throw new PdfLoadException(
                "This file could not be opened as a PDF. It may be corrupted, password-protected, " +
                "or not a PDF at all.\n\nDetails: " + ex.Message, ex);
        }

        if (pageCount <= 0)
            throw new PdfLoadException("The PDF reports zero pages, so there is nothing to print.");

        // ---- Optional metadata -------------------------------------------------
        string? title = null, author = null, subject = null, producer = null, creator = null, version = null;
        DateTimeOffset? created = null;
        var encrypted = false;

        try
        {
            // Import rather than InformationOnly: the latter is documented as unimplemented
            // in PDFsharp 6 and silently returns an empty information dictionary.
            using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            var info = doc.Info;
            title = Clean(info.Title);
            author = Clean(info.Author);
            subject = Clean(info.Subject);
            producer = Clean(info.Producer);
            creator = Clean(info.Creator);
            version = $"1.{doc.Version % 10}";
            encrypted = doc.SecuritySettings.IsEncrypted;

            if (info.CreationDate != default)
                created = new DateTimeOffset(info.CreationDate.ToUniversalTime(), TimeSpan.Zero);

            if (doc.PageCount != pageCount)
            {
                // Trust PDFium, since PDFium is what renders. Record the discrepancy.
                warnings.Add($"Page count differs between PDF readers ({pageCount} vs {doc.PageCount}); " +
                             $"using {pageCount}.");
                Log.Warning("Page count mismatch for {File}: pdfium={A} pdfsharp={B}", path, pageCount, doc.PageCount);
            }
        }
        catch (Exception ex)
        {
            warnings.Add("Document properties could not be read; printing is unaffected.");
            Log.Debug(ex, "Metadata read failed for {File}", path);
        }

        return new PdfDocumentInfo
        {
            Path = file.FullName,
            FileName = file.Name,
            FileSizeBytes = file.Length,
            Sha256 = ComputeSha256(path),
            PageCount = pageCount,
            Title = title,
            Author = author,
            Subject = subject,
            Producer = producer,
            Creator = creator,
            PdfVersion = version,
            CreatedUtc = created,
            IsEncrypted = encrypted,
            FirstPageWidthPt = firstWidth,
            FirstPageHeightPt = firstHeight,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Cheap structural check before handing the file to a parser, so that obviously wrong
    /// files produce a clear message instead of a library exception.
    /// </summary>
    private static bool LooksLikePdf(string path, out string? problem)
    {
        problem = null;
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[5];
            if (stream.Read(header) < 5 || !header.SequenceEqual("%PDF-"u8))
            {
                problem = "This file does not start with a PDF header, so it is not a PDF.";
                return false;
            }
            return true;
        }
        catch (IOException ex)
        {
            problem = $"The file could not be read. It may be open in another program.\n\nDetails: {ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            problem = $"Permission denied reading the file.\n\nDetails: {ex.Message}";
            return false;
        }
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    /// <summary>
    /// Confirms the source file is still the document the job was planned against.
    /// A changed file means the page numbers in the plan no longer mean anything.
    /// </summary>
    public static SourceVerification VerifySource(string path, string expectedSha256, long expectedSize)
    {
        if (!File.Exists(path)) return SourceVerification.Missing;
        try
        {
            var info = new FileInfo(path);
            if (info.Length != expectedSize) return SourceVerification.Changed;
            return string.Equals(ComputeSha256(path), expectedSha256, StringComparison.OrdinalIgnoreCase)
                ? SourceVerification.Unchanged
                : SourceVerification.Changed;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not verify source file {Path}", path);
            return SourceVerification.Unreadable;
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public enum SourceVerification
{
    Unchanged,
    Changed,
    Missing,
    Unreadable,
}
