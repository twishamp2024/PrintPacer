using PrintPacer.Core.Spooler;

namespace PrintPacer.Core.Simulation;

/// <summary>
/// A software model of a printer and its Windows queue.
///
/// This exists so that the parts of the application that must never get it wrong - batch
/// sequencing, completion verification, crash recovery, duplicate avoidance - can be tested
/// against failures that are impractical to stage with real hardware: a printer that dies
/// half way through page 37, a queue that stops answering, a page counter that lags behind
/// the spooler, a jam on the third sheet of the second batch.
///
/// The model tracks a physical impression counter exactly as the real printer does, so the
/// verification logic is exercised through the same code path as in production.
/// </summary>
public sealed class SimulatedPrinter
{
    private readonly object _gate = new();
    private readonly List<SimulatedJob> _jobs = new();
    private int _nextJobId = 1;

    public SimulatedPrinter(string name = "Simulated Printer") => Name = name;

    public string Name { get; }

    // ---- Controllable world state ------------------------------------------

    /// <summary>When false, the queue behaves as if the printer had been removed from Windows.</summary>
    public bool ExistsInWindows { get; set; } = true;

    /// <summary>When false, EnumJobs fails, modelling a spooler that cannot be queried.</summary>
    public bool QueueQueryable { get; set; } = true;

    public PrinterAvailability Availability { get; set; } = PrinterAvailability.Ready;

    /// <summary>When false the device probe reports unreachable, as a sleeping network printer does.</summary>
    public bool DeviceReachable { get; set; } = true;

    public Device.DeviceState DeviceState { get; set; } = Device.DeviceState.Idle;
    public List<string> DeviceStateReasons { get; } = new();

    /// <summary>Set to null to model a printer with no physical page counter at all.</summary>
    public long? ImpressionCounter { get; set; } = 1000;

    public long JamEvents { get; set; }
    public long MispickEvents { get; set; }

    /// <summary>Pages the engine advances per <see cref="Advance"/> call.</summary>
    public int PagesPerAdvance { get; set; } = 10;

    /// <summary>
    /// Stops the current job dead after this many of its pages, modelling the real failure
    /// this application exists to work around. Null means never.
    /// </summary>
    public int? StallAfterPages { get; set; }

    /// <summary>
    /// Delays the physical counter behind the spooler by this many advance calls, modelling
    /// a device whose management interface updates lazily.
    /// </summary>
    public int CounterLagAdvances { get; set; }

    /// <summary>When true, finished jobs stay in the queue with the retained flag, as Print to PDF does.</summary>
    public bool RetainFinishedJobs { get; set; }

    /// <summary>
    /// How many advance steps a finished job lingers in the queue before Windows removes it.
    /// Real spoolers keep a completed job visible briefly; without that window a monitor
    /// polling the queue could never observe the final page count. Set to 1 to model a
    /// spooler that whisks jobs away the instant they finish.
    /// </summary>
    public int FinishedLingerAdvances { get; set; } = 3;

    /// <summary>
    /// Stops the physical counter from advancing while the queue still reports progress,
    /// modelling a device whose management interface disagrees with Windows. The engine
    /// must refuse to choose between the two.
    /// </summary>
    public bool FreezeCounter { get; set; }

    /// <summary>Jobs the printer physically printed but which never reached the spooler view.</summary>
    public int TotalPagesPrinted { get; private set; }

    private readonly Queue<long> _pendingCounterUpdates = new();

    // ---- Queue operations used by the fake spooler and engine ---------------

    public SimulatedJob Submit(string document, int totalPages)
    {
        lock (_gate)
        {
            var job = new SimulatedJob
            {
                JobId = _nextJobId++,
                Document = document,
                TotalPages = totalPages,
                Status = NativeStatus.Spooling,
            };
            _jobs.Add(job);
            return job;
        }
    }

    public IReadOnlyList<SimulatedJob> Snapshot()
    {
        lock (_gate) return _jobs.Select(j => j.Clone()).ToList();
    }

    public bool Cancel(int jobId)
    {
        lock (_gate)
        {
            var job = _jobs.FirstOrDefault(j => j.JobId == jobId);
            if (job is null) return false;
            _jobs.Remove(job);
            return true;
        }
    }

    /// <summary>Removes a job without printing it, modelling a queue purge or a crash in the spooler.</summary>
    public void VanishJob(string document)
    {
        lock (_gate) _jobs.RemoveAll(j => j.Document == document);
    }

    /// <summary>
    /// Moves simulated time forward one step: the head job prints some pages and the
    /// physical counter follows, subject to the configured lag.
    /// </summary>
    public void Advance()
    {
        lock (_gate)
        {
            ApplyPendingCounterUpdates();

            // Windows leaves a finished job visible for a short while with its final page
            // count and the printed flag set, before removing it. Reproducing that matters:
            // without it, verification would never see a completed job in the queue.
            var settling = _jobs.FirstOrDefault(j => j.Status == NativeStatus.Printed);
            if (settling is not null)
            {
                if (++settling.LingerSteps >= FinishedLingerAdvances) _jobs.Remove(settling);
                return;
            }

            var job = _jobs.FirstOrDefault(j => j.Status is NativeStatus.Spooling or NativeStatus.Printing);
            if (job is null) return;

            if (job.Status == NativeStatus.Spooling)
            {
                job.Status = NativeStatus.Printing;
                return;
            }

            if (Availability is PrinterAvailability.Offline or PrinterAvailability.Error
                or PrinterAvailability.Paused)
            {
                return;
            }

            var remaining = job.TotalPages - job.PagesPrinted;
            var step = Math.Min(PagesPerAdvance, remaining);

            if (StallAfterPages is { } stallAt && job.PagesPrinted + step > stallAt)
                step = Math.Max(0, stallAt - job.PagesPrinted);

            if (step <= 0)
            {
                if (StallAfterPages is not null)
                {
                    // Stalled: the job sits in the queue making no progress, exactly like a
                    // real printer that has stopped mid-batch.
                    job.Status = NativeStatus.Printing;
                    JamEvents++;
                }
                return;
            }

            job.PagesPrinted += step;
            TotalPagesPrinted += step;
            QueueCounterUpdate(step);

            if (job.PagesPrinted >= job.TotalPages)
            {
                // Either kept forever (retention on) or kept for one more step and then
                // removed by the block at the top of this method.
                job.Status = RetainFinishedJobs ? NativeStatus.Retained : NativeStatus.Printed;
            }
        }
    }

    /// <summary>Runs <paramref name="steps"/> advance calls, for convenience in tests.</summary>
    public void Advance(int steps)
    {
        for (var i = 0; i < steps; i++) Advance();
    }

    /// <summary>Runs until the queue is empty of active work, with a bound so a stall cannot hang a test.</summary>
    public void RunToIdle(int maxSteps = 5000)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            lock (_gate)
            {
                if (_pendingCounterUpdates.Count == 0 &&
                    !_jobs.Any(j => j.Status is NativeStatus.Spooling or NativeStatus.Printing))
                {
                    return;
                }
            }
            Advance();
        }
    }

    private void QueueCounterUpdate(int pages)
    {
        if (ImpressionCounter is null || FreezeCounter) return;

        if (CounterLagAdvances <= 0)
        {
            ImpressionCounter += pages;
            return;
        }

        _pendingCounterUpdates.Enqueue(pages);
        while (_pendingCounterUpdates.Count > CounterLagAdvances)
            ImpressionCounter += _pendingCounterUpdates.Dequeue();
    }

    private void ApplyPendingCounterUpdates()
    {
        // Called each advance so a settled queue eventually catches up.
        if (ImpressionCounter is null || _pendingCounterUpdates.Count == 0) return;
        if (_jobs.Any(j => j.Status is NativeStatus.Spooling or NativeStatus.Printing)) return;

        while (_pendingCounterUpdates.Count > 0)
            ImpressionCounter += _pendingCounterUpdates.Dequeue();
    }
}

public enum NativeStatus
{
    Queued,
    Spooling,
    Printing,
    Printed,
    Retained,
    Error,
    Paused,
}

public sealed class SimulatedJob
{
    public int JobId { get; set; }
    public string Document { get; set; } = "";
    public int TotalPages { get; set; }
    public int PagesPrinted { get; set; }
    public NativeStatus Status { get; set; }

    /// <summary>Advance steps spent sitting in the queue after finishing.</summary>
    public int LingerSteps { get; set; }

    public SimulatedJob Clone() => (SimulatedJob)MemberwiseClone();
}
