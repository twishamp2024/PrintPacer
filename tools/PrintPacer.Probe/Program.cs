using Serilog;
using Docnet.Core;
using Docnet.Core.Models;
using PrintPacer.Core.Device;
using PrintPacer.Core.Model;
using PrintPacer.Core.Pdf;
using PrintPacer.Core.Printing;
using PrintPacer.Core.Spooler;
using PrintPacer.Core.Simulation;
using PrintPacer.Probe;

// Diagnostic and verification harness used during development and by the test suite.
// Not shipped in the installer.

// Debug-level console logging, so the library's own diagnostics are visible here.
Serilog.Log.Logger = new Serilog.LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: "    [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

switch (command)
{
    case "spooler": Spooler(); break;
    case "device": await DeviceAsync(); break;
    case "makepdfs": MakePdfs(); break;
    case "info": Info(); break;
    case "print": await PrintAsync(); break;
    case "colour": Colour(); break;
    case "verify": Verify(); break;
    case "rendercheck": RenderCheck(); break;
    case "makeicon": IconBuilder.Write(args[1]); Console.WriteLine($"  wrote {args[1]}"); break;
    case "png": Png(); break;
    case "makepdf":
        TestPdfBuilder.Write(args[1], int.Parse(args[2]), args.Length > 3 ? args[3] : $"PrintPacer fixture - {args[2]} pages");
        Console.WriteLine($"  wrote {args[1]} ({args[2]} pages)");
        break;
    default:
        Console.WriteLine("""
            PrintPacer diagnostic harness

              spooler                              dump printers and queues
              device [printerName]                 resolve address and probe the device
              makepdfs <outDir>                    generate the test fixture PDFs
              info <file.pdf>                      inspect a PDF the way the app does
              print <pdf> <first> <last> <printer> [outFile] [--bw]
                                                   submit one batch as one spooler job
              colour [printerName]                 ask each driver whether it accepts black & white
              verify <printed.pdf> <source.pdf> [expectedFirst expectedLast]
                                                   identify which source pages were printed
              png <file.pdf> <outDir> [dpi]        render each page to a PNG, to look at a layout
            """);
        break;
}

return;

void Spooler()
{
    var spooler = new WindowsSpooler();
    Console.WriteLine("=== Printers ===");
    foreach (var name in spooler.ListPrinterNames())
    {
        var s = spooler.GetPrinter(name);
        Console.WriteLine($"  {name}");
        Console.WriteLine($"     exists={s.Exists} avail={s.Availability} raw=0x{s.RawStatus:X8} " +
                          $"attrs=0x{s.Attributes:X8} workOffline={s.WorkOffline} jobs={s.QueuedJobCount}");
        Console.WriteLine($"     port={s.PortName} driver={s.DriverName}");
        Console.WriteLine($"     status=\"{s.StatusText}\" err={s.QueryError ?? "-"}");
    }

    Console.WriteLine();
    Console.WriteLine("=== Jobs ===");
    foreach (var name in spooler.ListPrinterNames())
    {
        var jobs = spooler.ListJobs(name, out var ok);
        if (jobs.Count == 0 && ok) continue;
        Console.WriteLine($"  {name}: querySucceeded={ok} count={jobs.Count}");
        foreach (var j in jobs)
            Console.WriteLine($"     #{j.JobId} \"{j.Document}\" status=0x{j.Status:X4} [{j.StatusText}] " +
                              $"pages={j.PagesPrinted}/{j.TotalPages} driverStatus={j.DriverStatus ?? "-"}");
    }

    Console.WriteLine();
    var missing = spooler.GetPrinter("This Printer Does Not Exist 12345");
    Console.WriteLine($"missing printer -> exists={missing.Exists} avail={missing.Availability}");
    spooler.ListJobs("This Printer Does Not Exist 12345", out var missingOk);
    Console.WriteLine($"missing printer -> jobs querySucceeded={missingOk}  (must be False)");
}

async Task DeviceAsync()
{
    var spooler = new WindowsSpooler();
    var printerName = args.Length > 1 ? args[1] : "HPDA95ED (HP Ink Tank Wireless 410 series)";
    var snapshot = spooler.GetPrinter(printerName);

    Console.WriteLine($"printer : {printerName}");
    Console.WriteLine($"port    : {snapshot.PortName}");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var address = await DeviceAddressResolver.ResolveAsync(snapshot.PortName, printerName, CancellationToken.None);
    Console.WriteLine($"resolved: {address ?? "(not found)"}   [{sw.ElapsedMilliseconds} ms]");
    if (address is null) return;

    using var probe = new NetworkDeviceProbe(address);
    sw.Restart();
    var d = await probe.ProbeAsync(CancellationToken.None);
    Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] source={d.Source}");
    Console.WriteLine($"  state={d.State} ({d.Describe()})");
    Console.WriteLine($"  reasons          : {(d.StateReasons.Count == 0 ? "-" : string.Join(" | ", d.StateReasons))}");
    Console.WriteLine($"  accepting        : {d.AcceptingJobs?.ToString() ?? "-"}   queued: {d.QueuedJobCount?.ToString() ?? "-"}");
    Console.WriteLine($"  model/serial     : {d.Model ?? "-"} / {d.SerialNumber ?? "-"}");
    Console.WriteLine($"  printAppImpress. : {d.PrintAppImpressions?.ToString() ?? "-"}");
    Console.WriteLine($"  engineImpress.   : {d.EngineImpressions?.ToString() ?? "-"}");
    Console.WriteLine($"  jams / mispicks  : {d.JamEvents?.ToString() ?? "-"} / {d.MispickEvents?.ToString() ?? "-"}");
}

void MakePdfs()
{
    var dir = args.Length > 1 ? args[1] : ".";
    Directory.CreateDirectory(dir);

    var fixtures = new (string Name, int Pages)[]
    {
        ("pages-1.pdf", 1),
        ("pages-10.pdf", 10),
        ("pages-50.pdf", 50),
        ("pages-51.pdf", 51),
        ("pages-200.pdf", 200),
        ("pages-203.pdf", 203),
    };

    foreach (var (name, pages) in fixtures)
    {
        var path = Path.Combine(dir, name);
        TestPdfBuilder.Write(path, pages, $"PrintPacer fixture - {pages} page(s)");
        Console.WriteLine($"  wrote {path}  ({pages} pages, {new FileInfo(path).Length:N0} bytes)");
    }

    // A file that is not a PDF at all.
    File.WriteAllText(Path.Combine(dir, "not-a-pdf.pdf"), "This is plainly not a PDF.");
    Console.WriteLine($"  wrote {Path.Combine(dir, "not-a-pdf.pdf")}  (invalid, for error-handling tests)");

    // A PDF that starts correctly but is truncated mid-body.
    var good = File.ReadAllBytes(Path.Combine(dir, "pages-10.pdf"));
    File.WriteAllBytes(Path.Combine(dir, "truncated.pdf"), good.Take(good.Length / 3).ToArray());
    Console.WriteLine($"  wrote {Path.Combine(dir, "truncated.pdf")}  (corrupt, for error-handling tests)");

    File.WriteAllBytes(Path.Combine(dir, "empty.pdf"), Array.Empty<byte>());
    Console.WriteLine($"  wrote {Path.Combine(dir, "empty.pdf")}  (zero bytes)");
}

void Info()
{
    var path = args[1];
    var service = new PdfService();
    try
    {
        var info = service.Load(path);
        Console.WriteLine($"  file     : {info.FileName}  ({info.SizeDisplay})");
        Console.WriteLine($"  pages    : {info.PageCount}");
        Console.WriteLine($"  size     : {info.PageSizeDisplay ?? "-"}");
        Console.WriteLine($"  title    : {info.Title ?? "-"}");
        Console.WriteLine($"  author   : {info.Author ?? "-"}");
        Console.WriteLine($"  producer : {info.Producer ?? "-"}");
        Console.WriteLine($"  version  : {info.PdfVersion ?? "-"}");
        Console.WriteLine($"  encrypted: {info.IsEncrypted}");
        Console.WriteLine($"  sha256   : {info.Sha256[..16]}...");
        foreach (var w in info.Warnings) Console.WriteLine($"  warning  : {w}");
    }
    catch (PdfLoadException ex)
    {
        Console.WriteLine($"  REJECTED : {ex.Message.Replace("\n", "\n             ")}");
    }
}

// Read-only: builds each driver's DEVMODE for a black and white job, exactly as a batch
// would, and reports whether the monochrome setting survived. Nothing is printed.
void Colour()
{
    var spooler = new WindowsSpooler();
    var names = args.Length > 1 ? new[] { args[1] } : spooler.ListPrinterNames().ToArray();
    foreach (var name in names)
    {
        var check = PrinterColour.Check(name);
        Console.WriteLine($"  {name}");
        Console.WriteLine($"      black & white: {check.Support}{(check.Detail is null ? "" : $" ({check.Detail})")}");
        if (check.Warning(name) is { } warning) Console.WriteLine($"      warning      : {warning}");
    }
}

async Task PrintAsync()
{
    var positional = args.Where(a => !a.StartsWith("--")).ToArray();
    var source = positional[1];
    var first = int.Parse(positional[2]);
    var last = int.Parse(positional[3]);
    var printer = positional[4];
    var outFile = positional.Length > 5 ? positional[5] : null;
    var mode = args.Contains("--bw") ? ColourMode.BlackAndWhite : ColourMode.Colour;

    var token = $"PrintPacer TEST {Guid.NewGuid():N}"[..40];
    var engine = new PdfiumPrintEngine();

    var request = new PrintRequest
    {
        SourcePath = Path.GetFullPath(source),
        FirstPage = first,
        LastPage = last,
        PrinterName = printer,
        DocumentToken = token,
        RenderDpi = 300,
        ColourMode = mode,
        OutputFilePath = outFile is null ? null : Path.GetFullPath(outFile),
    };

    Console.WriteLine($"token   : {token}");
    Console.WriteLine($"range   : {first}-{last} ({request.PageCount} pages)");
    Console.WriteLine($"printer : {printer}");
    Console.WriteLine($"mode    : {mode.Describe()}");

    // Watch the spooler while the job is in flight, to prove the token is discoverable.
    using var cts = new CancellationTokenSource();
    var spooler = new WindowsSpooler();
    var watcher = Task.Run(async () =>
    {
        var seen = new HashSet<string>();
        while (!cts.IsCancellationRequested)
        {
            foreach (var j in spooler.ListJobs(printer, out var ok).Where(j => j.Document == token))
            {
                var colour = j.Monochrome switch { true => "monochrome", false => "colour", null => "no colour setting" };
                var line = $"    spooler: #{j.JobId} [{j.StatusText}] {j.PagesPrinted}/{j.TotalPages} DEVMODE {colour}";
                if (seen.Add(line)) Console.WriteLine(line);
            }
            try { await Task.Delay(300, cts.Token); } catch (OperationCanceledException) { break; }
        }
    });

    if (outFile is not null) Console.WriteLine($"outFile : {Path.GetFullPath(outFile)}");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await engine.SubmitAsync(request, CancellationToken.None);

    cts.Cancel();
    await watcher;

    Console.WriteLine($"spooled : {result.Spooled}  pages={result.PagesRendered}  " +
                      $"[{sw.ElapsedMilliseconds} ms]");
    if (result.Error is not null) Console.WriteLine($"error   : {result.Error}");
    if (result.ColourWarning is not null) Console.WriteLine($"warning : {result.ColourWarning}");
}

// Confirms what PDFium actually puts in the buffer: in particular whether the page
// background arrives as opaque white or as transparent pixels, which decides how the
// bitmap may safely be composited onto a printer device context.
void RenderCheck()
{
    var path = args[1];
    var pageNumber = args.Length > 2 ? int.Parse(args[2]) : 1;
    var dpi = args.Length > 3 ? int.Parse(args[3]) : 300;

    using var reader = DocLib.Instance.GetDocReader(path, new PageDimensions(dpi / 72.0));
    using var page = reader.GetPageReader(pageNumber - 1);

    var w = page.GetPageWidth();
    var h = page.GetPageHeight();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var bgra = page.GetImage(Docnet.Core.Models.RenderFlags.RenderForPrinting |
                            Docnet.Core.Models.RenderFlags.LimitImageCacheSize);
    sw.Stop();

    Console.WriteLine($"  size        : {w} x {h} px at {dpi} dpi");
    Console.WriteLine($"  buffer      : {bgra.LongLength:N0} bytes (expected {(long)w * h * 4:N0})");
    Console.WriteLine($"  render time : {sw.ElapsedMilliseconds} ms");

    // Sample a few points that must be blank paper, and the very centre which has ink.
    void Sample(string label, int x, int y)
    {
        var i = (y * w + x) * 4;
        Console.WriteLine($"  {label,-12}: B={bgra[i]} G={bgra[i + 1]} R={bgra[i + 2]} A={bgra[i + 3]}");
    }

    Sample("top-left", 2, 2);
    Sample("mid-margin", w / 2, 8);
    Sample("centre", w / 2, h / 2);

    // Decide whether the buffer uses straight or premultiplied alpha. With premultiplied
    // alpha every channel is <= alpha by construction; a single counter-example proves the
    // buffer is straight. This determines how the page may legally be composited onto paper.
    long partial = 0, violations = 0, opaque = 0, transparent = 0;
    var worst = 0;
    for (long i = 0; i + 3 < bgra.LongLength; i += 4)
    {
        var a = bgra[i + 3];
        if (a == 0) { transparent++; continue; }
        if (a == 255) { opaque++; continue; }
        partial++;
        var maxChannel = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2]));
        if (maxChannel > a)
        {
            violations++;
            worst = Math.Max(worst, maxChannel - a);
        }
    }

    Console.WriteLine($"  pixels      : transparent={transparent:N0} partial={partial:N0} opaque={opaque:N0}");
    Console.WriteLine($"  alpha model : {(violations > 0 ? "STRAIGHT (non-premultiplied)" : "premultiplied or indeterminate")}" +
                      $"   violations={violations:N0} worstExcess={worst}");
}

// Identifies which source page each printed page actually is.
//
// The printed output is rasterised, so there is no text to extract. Instead every page of
// the source and of the output is reduced to a small grayscale fingerprint and each output
// page is matched to its nearest source page. Because each fixture page carries a large
// unique digit, the match is unambiguous - and this checks the thing that actually matters:
// that the ink on sheet 1 of the batch is the ink from page 51 of the document.
void Verify()
{
    var outputPath = args[1];
    var sourcePath = args[2];

    var sourcePrints = Fingerprint(sourcePath);
    var outputPrints = Fingerprint(outputPath);

    Console.WriteLine($"  source pages : {sourcePrints.Count}");
    Console.WriteLine($"  printed pages: {outputPrints.Count}");

    var matched = new List<int>();
    var worstConfidence = double.MaxValue;

    foreach (var printed in outputPrints)
    {
        var ranked = sourcePrints
            .Select((fingerprint, index) => (Page: index + 1, Distance: Distance(printed, fingerprint)))
            .OrderBy(x => x.Distance)
            .ToArray();

        matched.Add(ranked[0].Page);

        // Confidence is how much better the best match is than the runner-up. Anything
        // near 1.0 would mean the fingerprint cannot actually tell the pages apart.
        if (ranked.Length > 1 && ranked[1].Distance > 0)
            worstConfidence = Math.Min(worstConfidence, ranked[1].Distance / Math.Max(ranked[0].Distance, 1e-6));
    }

    Console.WriteLine($"  identified   : {Summarise(matched)}");
    Console.WriteLine($"  separation   : {(worstConfidence == double.MaxValue ? "n/a" : $"{worstConfidence:0.0}x (runner-up vs best)")}");

    if (args.Length >= 5)
    {
        var expectedFirst = int.Parse(args[3]);
        var expectedLast = int.Parse(args[4]);
        var expected = Enumerable.Range(expectedFirst, expectedLast - expectedFirst + 1).ToArray();

        var correctPages = matched.SequenceEqual(expected);
        var confident = worstConfidence == double.MaxValue || worstConfidence > 1.5;

        Console.WriteLine($"  expected     : {Summarise(expected.ToList())}");
        if (correctPages && confident)
        {
            Console.WriteLine("  RESULT: PASS - exact page range, correct order, no duplicates, no gaps");
        }
        else
        {
            Console.WriteLine(correctPages
                ? "  RESULT: FAIL - pages match but the fingerprints are not separated enough to trust"
                : "  RESULT: FAIL - printed pages are not the expected source pages");
            Environment.ExitCode = 1;
        }
    }
}

static string Summarise(List<int> pages)
{
    if (pages.Count == 0) return "(none)";
    var contiguous = pages.Select((p, i) => p - i).Distinct().Count() == 1;
    return contiguous && pages.Count > 3
        ? $"{pages[0]}..{pages[^1]} ({pages.Count} pages, contiguous ascending)"
        : string.Join(",", pages);
}

// 24x32 grayscale average-pool fingerprint of each page.
static List<float[]> Fingerprint(string path)
{
    const int gridWidth = 24, gridHeight = 32, renderDpi = 36;
    var results = new List<float[]>();

    using var reader = DocLib.Instance.GetDocReader(path, new PageDimensions(renderDpi / 72.0));
    var count = reader.GetPageCount();

    for (var i = 0; i < count; i++)
    {
        using var page = reader.GetPageReader(i);
        var w = page.GetPageWidth();
        var h = page.GetPageHeight();
        var bgra = page.GetImage(Docnet.Core.Models.RenderFlags.RenderForPrinting);

        var sums = new float[gridWidth * gridHeight];
        var counts = new int[gridWidth * gridHeight];

        for (var y = 0; y < h; y++)
        {
            var cellY = Math.Min(gridHeight - 1, y * gridHeight / h);
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                if (o + 3 >= bgra.Length) continue;

                // Flatten onto white exactly as the print path does, so source and printed
                // fingerprints live in the same space.
                var a = bgra[o + 3] / 255f;
                var grey = (0.114f * bgra[o] + 0.587f * bgra[o + 1] + 0.299f * bgra[o + 2]) * a + 255f * (1 - a);

                var cell = cellY * gridWidth + Math.Min(gridWidth - 1, x * gridWidth / w);
                sums[cell] += grey;
                counts[cell]++;
            }
        }

        for (var c = 0; c < sums.Length; c++)
            if (counts[c] > 0) sums[c] /= counts[c];

        results.Add(sums);
    }

    return results;
}

static double Distance(float[] a, float[] b)
{
    double total = 0;
    for (var i = 0; i < a.Length; i++)
    {
        var d = a[i] - b[i];
        total += d * d;
    }
    return Math.Sqrt(total / a.Length);
}

void Png()
{
    var source = args[1];
    var outDir = args[2];
    var dpi = args.Length > 3 ? double.Parse(args[3]) : 60;
    Directory.CreateDirectory(outDir);

    var pages = OutputInspector.Render(source, dpi);
    for (var i = 0; i < pages.Count; i++)
    {
        var page = pages[i];
        using var bitmap = new System.Drawing.Bitmap(page.PixelWidth, page.PixelHeight);
        for (var y = 0; y < page.PixelHeight; y++)
        for (var x = 0; x < page.PixelWidth; x++)
        {
            var v = page.Luminance[y * page.PixelWidth + x];
            bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(v, v, v));
        }

        var path = Path.Combine(outDir, $"page-{i + 1}.png");
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"  {path}  {page.WidthPt:0} x {page.HeightPt:0} pt");
    }
}
