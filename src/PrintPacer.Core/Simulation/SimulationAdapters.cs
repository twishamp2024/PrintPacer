using PrintPacer.Core.Device;
using PrintPacer.Core.Engine;
using PrintPacer.Core.Model;
using PrintPacer.Core.Printing;
using PrintPacer.Core.Spooler;

namespace PrintPacer.Core.Simulation;

/// <summary>Presents a <see cref="SimulatedPrinter"/> through the real <see cref="ISpooler"/> interface.</summary>
public sealed class SimulatedSpooler : ISpooler
{
    private readonly SimulatedPrinter _printer;

    public SimulatedSpooler(SimulatedPrinter printer) => _printer = printer;

    public IReadOnlyList<string> ListPrinterNames() =>
        _printer.ExistsInWindows ? new[] { _printer.Name } : Array.Empty<string>();

    public string? GetDefaultPrinterName() => _printer.ExistsInWindows ? _printer.Name : null;

    public PrinterSnapshot GetPrinter(string printerName)
    {
        if (!_printer.ExistsInWindows || printerName != _printer.Name)
            return PrinterSnapshot.Missing(printerName);

        return new PrinterSnapshot
        {
            Name = printerName,
            Exists = true,
            Availability = _printer.Availability,
            StatusText = _printer.Availability.ToString(),
            PortName = "SIM:",
            DriverName = "Simulated driver",
            QueuedJobCount = _printer.Snapshot().Count,
        };
    }

    public IReadOnlyList<SpoolerJob> ListJobs(string printerName, out bool querySucceeded)
    {
        if (!_printer.ExistsInWindows || printerName != _printer.Name || !_printer.QueueQueryable)
        {
            querySucceeded = false;
            return Array.Empty<SpoolerJob>();
        }

        querySucceeded = true;
        return _printer.ReadQueue().Select(j => new SpoolerJob
        {
            JobId = j.JobId,
            Document = j.Document,
            UserName = "simulation",
            TotalPages = j.TotalPages,
            PagesPrinted = j.PagesPrinted,
            Monochrome = j.Monochrome,
            Status = MapStatus(j.Status),
            StatusText = j.Status.ToString(),
        }).ToList();
    }

    public bool CancelJob(string printerName, int jobId, out string? error)
    {
        error = null;
        return _printer.Cancel(jobId);
    }

    private static uint MapStatus(NativeStatus status) => status switch
    {
        NativeStatus.Spooling => 0x00000008,                   // JOB_STATUS_SPOOLING
        NativeStatus.Printing => 0x00000010,                   // JOB_STATUS_PRINTING
        NativeStatus.Printed => 0x00000080,                    // JOB_STATUS_PRINTED
        NativeStatus.Retained => 0x00002000 | 0x00000080,      // RETAINED | PRINTED
        NativeStatus.Error => 0x00000002,                      // JOB_STATUS_ERROR
        NativeStatus.Paused => 0x00000001,                     // JOB_STATUS_PAUSED
        _ => 0,
    };
}

/// <summary>
/// A print engine that creates jobs in a <see cref="SimulatedPrinter"/> instead of on paper,
/// with switches for the submission failures that matter.
/// </summary>
public sealed class SimulatedPrintEngine : IPrintEngine
{
    private readonly SimulatedPrinter _printer;

    public SimulatedPrintEngine(SimulatedPrinter printer) => _printer = printer;

    public string Name => "Simulated";

    /// <summary>Fail before handing anything over. This is the safely retryable failure.</summary>
    public bool FailBeforeAnyOutput { get; set; }

    /// <summary>
    /// Create the job in the queue but report failure, modelling a crash after partial
    /// output. The engine must treat this as ambiguous, never as safely retryable.
    /// </summary>
    public bool FailAfterPartialOutput { get; set; }

    /// <summary>Artificial delay before the job appears, for testing the monitoring loop.</summary>
    public TimeSpan SubmitDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Models a driver with no black and white setting: the job carries no colour setting
    /// and the result says so, as <see cref="PdfiumPrintEngine"/> reports for such a driver.
    /// </summary>
    public bool DriverIgnoresMonochrome { get; set; }

    public int SubmissionCount { get; private set; }

    public List<string> SubmittedTokens { get; } = new();

    /// <summary>Every request exactly as the engine made it, so tests can check what each batch asked for.</summary>
    public List<PrintRequest> SubmittedRequests { get; } = new();

    public async Task<PrintResult> SubmitAsync(PrintRequest request, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        SubmissionCount++;
        SubmittedTokens.Add(request.DocumentToken);
        SubmittedRequests.Add(request);

        var blackAndWhite = request.ColourMode == ColourMode.BlackAndWhite;
        bool? monochrome = DriverIgnoresMonochrome ? null : blackAndWhite;

        if (SubmitDelay > TimeSpan.Zero)
            await Task.Delay(SubmitDelay, ct).ConfigureAwait(false);

        if (!_printer.ExistsInWindows)
            return PrintResult.Failure("Printer is not available.", started, beforeAnyOutput: true);

        if (FailBeforeAnyOutput)
            return PrintResult.Failure("Simulated failure before any output.", started, beforeAnyOutput: true);

        if (FailAfterPartialOutput)
        {
            // Some of the pages reached the spooler before the failure, so a job exists and
            // paper may well come out - just not all of it. This is the dangerous case the
            // engine must never resend on its own.
            var partial = Math.Max(1, request.PageCount / 2);
            _printer.Submit(request.DocumentToken, partial, monochrome);
            return PrintResult.Failure("Simulated failure after partial output.", started, beforeAnyOutput: false);
        }

        _printer.Submit(request.DocumentToken, request.PageCount, monochrome);

        return new PrintResult
        {
            Spooled = true,
            PagesRendered = request.PageCount,
            ColourWarning = blackAndWhite && DriverIgnoresMonochrome
                ? new MonochromeCheck(MonochromeSupport.NotExposed).Warning(request.PrinterName)
                : null,
            StartedUtc = started,
            FinishedUtc = DateTimeOffset.UtcNow,
        };
    }
}

/// <summary>Reports the simulated printer's state and physical counter.</summary>
public sealed class SimulatedDeviceProbe : IDeviceProbe
{
    private readonly SimulatedPrinter _printer;

    public SimulatedDeviceProbe(SimulatedPrinter printer) => _printer = printer;

    public bool IsConfigured { get; set; } = true;
    public string Description => "Simulated device";

    public int ProbeCount { get; private set; }

    public Task<DeviceSnapshot> ProbeAsync(CancellationToken ct)
    {
        ProbeCount++;

        if (!IsConfigured) return Task.FromResult(DeviceSnapshot.Unavailable("probe disabled"));
        if (!_printer.DeviceReachable) return Task.FromResult(DeviceSnapshot.Unavailable("simulated timeout"));

        return Task.FromResult(new DeviceSnapshot
        {
            Reachable = true,
            State = _printer.DeviceState,
            StateReasons = _printer.DeviceStateReasons.ToList(),
            AcceptingJobs = _printer.DeviceState != DeviceState.Stopped,
            PrintAppImpressions = _printer.ImpressionCounter,
            EngineImpressions = _printer.ImpressionCounter,
            JamEvents = _printer.JamEvents,
            MispickEvents = _printer.MispickEvents,
            Model = "Simulated Printer",
            Source = "Simulation",
        });
    }
}

public sealed class SimulatedDeviceProbeFactory : IDeviceProbeFactory
{
    private readonly SimulatedDeviceProbe _probe;

    public SimulatedDeviceProbeFactory(SimulatedDeviceProbe probe) => _probe = probe;

    public Task<IDeviceProbe> GetProbeAsync(string printerName, CancellationToken ct) =>
        Task.FromResult<IDeviceProbe>(_probe);

    public void Invalidate(string printerName) { }
}

/// <summary>A clock the test drives by hand, so an overnight schedule runs in milliseconds.</summary>
public sealed class ManualClock : ISystemClock
{
    private DateTimeOffset _utcNow;
    private TimeSpan _monotonic;

    public ManualClock(DateTimeOffset? start = null)
    {
        _utcNow = start ?? new DateTimeOffset(2026, 9, 20, 23, 0, 0, TimeSpan.Zero);
        _monotonic = TimeSpan.FromHours(1);
    }

    public DateTimeOffset UtcNow => _utcNow;
    public TimeSpan Monotonic => _monotonic;

    /// <summary>Normal passage of time: both clocks advance together.</summary>
    public void Advance(TimeSpan by)
    {
        _utcNow += by;
        _monotonic += by;
    }

    /// <summary>
    /// The machine slept: wall-clock time passed but the monotonic tick count did not,
    /// which is exactly how Windows behaves across suspend.
    /// </summary>
    public void Sleep(TimeSpan wallClockDuration) => _utcNow += wallClockDuration;

    /// <summary>Someone (or NTP) moved the system clock, without any real time passing.</summary>
    public void JumpClock(TimeSpan by) => _utcNow += by;

    public List<Notification> Noop { get; } = new();
}

/// <summary>Collects notifications so tests can assert on what the user would have been told.</summary>
public sealed class RecordingNotifier : INotifier
{
    public List<Notification> Notifications { get; } = new();
    public void Notify(Notification notification) => Notifications.Add(notification);
}

/// <summary>Records keep-awake requests so the power behaviour can be asserted.</summary>
public sealed class RecordingPowerManager : IPowerManager
{
    public bool IsHoldingRequest { get; private set; }
    public int AcquireCount { get; private set; }
    public int ReleaseCount { get; private set; }

    public void Acquire(string reason)
    {
        if (IsHoldingRequest) return;
        IsHoldingRequest = true;
        AcquireCount++;
    }

    public void Release()
    {
        if (!IsHoldingRequest) return;
        IsHoldingRequest = false;
        ReleaseCount++;
    }
}

/// <summary>Records every invoice it is asked to print, and can be told to refuse.</summary>
public sealed class SimulatedInvoicePrinter : IInvoicePrinter
{
    private readonly List<Billing.Invoice> _printed = new();

    /// <summary>When set, every request fails before anything is sent.</summary>
    public bool Fail { get; set; }

    /// <summary>When set, requests are accepted but never finish, as if we stopped mid-send.</summary>
    public bool Hold { get; set; }

    /// <summary>Every invoice handed over, whether or not it finished.</summary>
    public IReadOnlyList<Billing.Invoice> Printed
    {
        get { lock (_printed) return _printed.ToList(); }
    }

    public Task<PrintResult> PrintAsync(Billing.Invoice invoice, string printerName, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (Fail) return Task.FromResult(PrintResult.Failure("Simulated invoice failure.", now, beforeAnyOutput: true));

        lock (_printed) _printed.Add(invoice);
        if (Hold) return new TaskCompletionSource<PrintResult>().Task;

        return Task.FromResult(new PrintResult { Spooled = true, PagesRendered = 1, StartedUtc = now, FinishedUtc = now });
    }
}
