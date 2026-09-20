using FluentAssertions;
using PrintPacer.Core.Engine;
using PrintPacer.Core.Model;
using PrintPacer.Core.Spooler;
using Xunit;

namespace PrintPacer.Tests;

/// <summary>
/// End-to-end tests of the scheduler against a simulated printer.
///
/// These drive the real engine, the real SQLite store and the real verification logic.
/// Only the world outside the application (printer, queue, clock) is simulated, so a pass
/// here means the shipped code paths work, not a parallel implementation of them.
/// </summary>
public class JobEngineTests
{
    // ---- The core workflow ---------------------------------------------------

    [Fact]
    public async Task PrintsA200PageDocumentAsFourSeparateJobsWithRestsBetween()
    {
        await using var h = await EngineHarness.CreateAsync(200);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 60);

        h.Engine.StartJob(job.Id);

        for (var batch = 1; batch <= 4; batch++)
        {
            (await h.WaitForBatchStateAsync(batch, BatchState.Completed))
                .Should().BeTrue($"batch {batch} should complete");

            if (batch < 4)
            {
                (await h.WaitForStateAsync(JobState.CoolingDown))
                    .Should().BeTrue("the printer must be rested between batches");
                h.SkipDelay(61);
            }
        }

        (await h.WaitForStateAsync(JobState.Completed)).Should().BeTrue();

        // The defining requirement: four independent print jobs, not one big one.
        h.PrintEngine.SubmissionCount.Should().Be(4);
        h.PrintEngine.SubmittedTokens.Should().OnlyHaveUniqueItems();
        h.Printer.TotalPagesPrinted.Should().Be(200);

        h.Job.Batches.Select(b => (b.FirstPage, b.LastPage)).Should().Equal(
            (1, 50), (51, 100), (101, 150), (151, 200));
        h.Job.Batches.Should().OnlyContain(b => b.State == BatchState.Completed);
    }

    [Fact]
    public async Task ZeroDelayRunsBatchesBackToBack()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var job = await h.CreateJobAsync(pagesPerBatch: 3, delayMinutes: 0);

        h.Engine.StartJob(job.Id);

        (await h.WaitForStateAsync(JobState.Completed, 20_000)).Should().BeTrue();
        h.PrintEngine.SubmissionCount.Should().Be(4);   // 3 + 3 + 3 + 1
        h.Printer.TotalPagesPrinted.Should().Be(10);
    }

    [Fact]
    public async Task BatchSizeOfOneStillPrintsEveryPageExactlyOnce()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var job = await h.CreateJobAsync(pagesPerBatch: 1, delayMinutes: 0);

        h.Engine.StartJob(job.Id);

        (await h.WaitForStateAsync(JobState.Completed, 30_000)).Should().BeTrue();
        h.Job.Batches.Should().HaveCount(10);
        h.PrintEngine.SubmissionCount.Should().Be(10);
        h.Printer.TotalPagesPrinted.Should().Be(10);
    }

    [Fact]
    public async Task BatchSizeLargerThanTheDocumentPrintsOneBatch()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var job = await h.CreateJobAsync(pagesPerBatch: 500, delayMinutes: 60);

        h.Engine.StartJob(job.Id);

        (await h.WaitForStateAsync(JobState.Completed)).Should().BeTrue();
        h.Job.Batches.Should().HaveCount(1);
        h.Job.Batches[0].LastPage.Should().Be(10);
    }

    [Fact]
    public async Task ASinglePageDocumentPrints()
    {
        await using var h = await EngineHarness.CreateAsync(1);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 60);

        h.Engine.StartJob(job.Id);

        (await h.WaitForStateAsync(JobState.Completed)).Should().BeTrue();
        h.Printer.TotalPagesPrinted.Should().Be(1);
    }

    [Fact]
    public async Task APageCountNotDivisibleByTheBatchSizeEndsWithAShortBatch()
    {
        await using var h = await EngineHarness.CreateAsync(203);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);

        h.Engine.StartJob(job.Id);

        (await h.WaitForStateAsync(JobState.Completed, 30_000)).Should().BeTrue();
        h.Job.Batches.Should().HaveCount(5);
        h.Job.Batches[^1].PageCount.Should().Be(3);
        h.Printer.TotalPagesPrinted.Should().Be(203);
    }

    // ---- Scheduling ----------------------------------------------------------

    [Fact]
    public async Task AVeryLongDelayIsHonouredExactlyAndNotFiredEarly()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 12 * 60);

        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue();

        h.Clock.Advance(TimeSpan.FromHours(11));
        await Task.Delay(60);
        h.Job.State.Should().Be(JobState.CoolingDown, "11 hours is not yet 12");
        h.PrintEngine.SubmissionCount.Should().Be(1);

        h.Clock.Advance(TimeSpan.FromHours(1.1));
        (await h.WaitForStateAsync(JobState.Completed)).Should().BeTrue();
        h.PrintEngine.SubmissionCount.Should().Be(2);
    }

    [Fact]
    public async Task AScheduledStartTimeIsWaitedFor()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var startAt = h.Clock.UtcNow.AddHours(3);
        var job = await h.CreateJobAsync(10, 0, StartMode.AtTime, startAt);

        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.WaitingForStart)).Should().BeTrue();

        h.Clock.Advance(TimeSpan.FromHours(2));
        await Task.Delay(60);
        h.PrintEngine.SubmissionCount.Should().Be(0, "the start time has not arrived");

        h.Clock.Advance(TimeSpan.FromHours(1.1));
        (await h.WaitForStateAsync(JobState.Completed)).Should().BeTrue();
    }

    [Fact]
    public async Task TimeSpentAsleepCountsTowardsTheRestPeriod()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 60);

        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue();

        // The machine suspends: wall-clock time passes, the monotonic tick count does not.
        h.Clock.Sleep(TimeSpan.FromMinutes(90));

        (await h.WaitForStateAsync(JobState.Completed)).Should().BeTrue(
            "an hour of hibernation is still an hour of the printer resting");
    }

    [Fact]
    public async Task AClockMovedBackwardsDoesNotStrandTheJob()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 60);

        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue();

        // Real time passes normally, but then someone winds the clock back a year.
        h.Clock.Advance(TimeSpan.FromMinutes(61));
        h.Clock.JumpClock(TimeSpan.FromDays(-365));

        (await h.WaitForStateAsync(JobState.Completed, 15_000)).Should().BeTrue(
            "the monotonic backstop must fire the batch even though wall-clock time went backwards");
    }

    // ---- Printer problems ------------------------------------------------------

    [Fact]
    public async Task AnUnavailablePrinterBlocksInsteadOfSubmitting()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        h.Printer.Availability = PrinterAvailability.Offline;

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        h.Engine.StartJob(job.Id);

        (await h.WaitForStateAsync(JobState.BlockedPrinterOffline)).Should().BeTrue();
        h.PrintEngine.SubmissionCount.Should().Be(0, "nothing may be sent to an offline printer");
    }

    [Fact]
    public async Task ABlockedJobResumesOnceThePrinterComesBack()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        h.Printer.Availability = PrinterAvailability.Offline;

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.BlockedPrinterOffline)).Should().BeTrue();

        h.Printer.Availability = PrinterAvailability.Ready;
        h.Clock.Advance(TimeSpan.FromMinutes(6));   // past the retry interval

        (await h.WaitForStateAsync(JobState.Completed, 20_000)).Should().BeTrue();
        h.Printer.TotalPagesPrinted.Should().Be(100);
    }

    [Fact]
    public async Task ABlockedPrinterDoesNotAccumulateDuplicateJobs()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        h.Printer.Availability = PrinterAvailability.Offline;

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.BlockedPrinterOffline)).Should().BeTrue();

        // Sit blocked through many retry intervals.
        for (var i = 0; i < 10; i++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(6));
            await Task.Delay(40);
        }

        h.PrintEngine.SubmissionCount.Should().Be(0,
            "a blocked printer must never accumulate repeated submissions");
    }

    [Fact]
    public async Task APrinterRemovedFromWindowsStopsTheJobSafely()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 30);

        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue();

        h.Printer.ExistsInWindows = false;
        h.SkipDelay(31);

        (await h.WaitForStateAsync(JobState.BlockedPrinterError)).Should().BeTrue();
        h.PrintEngine.SubmissionCount.Should().Be(1);
    }

    [Fact]
    public async Task APrinterReportingAFaultBlocksEvenWhenWindowsSaysReady()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        h.Printer.DeviceState = PrintPacer.Core.Device.DeviceState.Stopped;
        h.Printer.DeviceStateReasons.Add("Out of paper");

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        h.Engine.StartJob(job.Id);

        (await h.WaitForStateAsync(JobState.BlockedPrinterError)).Should().BeTrue();
        h.PrintEngine.SubmissionCount.Should().Be(0);
        h.Job.LastPrinterStatus.Should().Contain("Out of paper");
    }

    [Fact]
    public async Task AnUnreachableDeviceDoesNotBlockAHealthyWindowsQueue()
    {
        // A sleeping network printer stops answering its management interface. That must
        // not be mistaken for a fault, or the job would never start.
        await using var h = await EngineHarness.CreateAsync(50);
        h.Printer.DeviceReachable = false;

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        h.Engine.StartJob(job.Id);

        (await h.WaitForStateAsync(JobState.Completed, 15_000)).Should().BeTrue();
    }

    [Fact]
    public async Task AnUnreadableQueueIsNotTreatedAsAFinishedJob()
    {
        await using var h = await EngineHarness.CreateAsync(50);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);

        h.Engine.StartJob(job.Id);
        (await h.WaitUntilAsync(() => h.PrintEngine.SubmissionCount == 1)).Should().BeTrue();

        h.Printer.QueueQueryable = false;
        await Task.Delay(300);

        h.Job.State.Should().Be(JobState.PrintingBatch,
            "being unable to ask is not the same as being told the job finished");
        h.PrintEngine.SubmissionCount.Should().Be(1);

        h.Printer.QueueQueryable = true;
        (await h.WaitForStateAsync(JobState.Completed, 15_000)).Should().BeTrue();
    }

    // ---- Stalls: the problem this application exists to manage -----------------

    [Fact]
    public async Task AStalledBatchStopsAndAsksRatherThanResending()
    {
        await using var h = await EngineHarness.CreateAsync(100, s => s.StallTimeoutMinutes = 10);
        h.Printer.PagesPerAdvance = 5;
        h.Printer.StallAfterPages = 17;     // dies part-way through the first batch

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 30);
        h.Engine.StartJob(job.Id);

        // Wait until the printer has stopped dead AND the engine has observed the final
        // page count, so that the stall clock is genuinely no longer being reset.
        (await h.WaitUntilAsync(() =>
            h.Job.Batches[0].SpoolerPagesPrinted == 17)).Should().BeTrue(h.Diagnostics());

        h.Clock.Advance(TimeSpan.FromMinutes(11));

        (await h.WaitForStateAsync(JobState.NeedsAttention, 15_000)).Should().BeTrue(h.Diagnostics());
        h.PrintEngine.SubmissionCount.Should().Be(1, "a stalled batch must never be resent automatically");
        h.Job.AttentionReason.Should().Contain("has not progressed");
        h.Job.AttentionReason.Should().Contain("17 of 50",
            "the user needs to know exactly how many sheets came out");
    }

    [Fact]
    public async Task APartiallyPrintedBatchCanBeResumedWithoutReprintingTheSheetsThatCameOut()
    {
        await using var h = await EngineHarness.CreateAsync(100, s => s.StallTimeoutMinutes = 10);
        h.Printer.PagesPerAdvance = 5;
        h.Printer.StallAfterPages = 20;

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        h.Engine.StartJob(job.Id);

        (await h.WaitUntilAsync(() =>
            h.Job.Batches[0].SpoolerPagesPrinted == 20)).Should().BeTrue(h.Diagnostics());
        h.Clock.Advance(TimeSpan.FromMinutes(11));
        (await h.WaitForStateAsync(JobState.NeedsAttention, 15_000)).Should().BeTrue(h.Diagnostics());

        // Clear the fault and tell the engine 20 sheets came out.
        h.Printer.StallAfterPages = null;
        h.Engine.CancelCurrentSpoolerJob(job.Id);
        await Task.Delay(100);
        h.Engine.ResumeBatchFrom(job.Id, 1, printedPages: 20);

        (await h.WaitUntilAsync(() => h.Job.Batches.Count == 3, 10_000)).Should().BeTrue();

        var remainder = h.Job.Batches.Single(b => b.Number == 3);
        remainder.FirstPage.Should().Be(21, "pages 1-20 already came out");
        remainder.LastPage.Should().Be(50);
        h.Job.Batches.Single(b => b.Number == 1).State.Should().Be(BatchState.Skipped);
    }

    // ---- Duplicate-print safety --------------------------------------------------

    [Fact]
    public async Task AFailureBeforeAnyOutputIsRetriedAutomatically()
    {
        await using var h = await EngineHarness.CreateAsync(50);
        h.PrintEngine.FailBeforeAnyOutput = true;

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        h.Engine.StartJob(job.Id);

        (await h.WaitUntilAsync(() => h.PrintEngine.SubmissionCount >= 1)).Should().BeTrue();
        (await h.WaitForStateAsync(JobState.BlockedPrinterOffline)).Should().BeTrue();

        h.PrintEngine.FailBeforeAnyOutput = false;
        h.Clock.Advance(TimeSpan.FromMinutes(6));

        (await h.WaitForStateAsync(JobState.Completed, 15_000)).Should().BeTrue();
        h.Printer.TotalPagesPrinted.Should().Be(50, "exactly one set of pages, despite the retry");
    }

    [Fact]
    public async Task AFailureAfterPartialOutputIsNeverRetriedAutomatically()
    {
        await using var h = await EngineHarness.CreateAsync(50);
        h.PrintEngine.FailAfterPartialOutput = true;

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        h.Engine.StartJob(job.Id);

        (await h.WaitForStateAsync(JobState.NeedsAttention, 20_000)).Should().BeTrue();
        h.PrintEngine.SubmissionCount.Should().Be(1,
            "pages may already be on paper, so this must never be resent without asking");
    }

    [Fact]
    public async Task AutomaticRetriesAreCappedAndThenEscalated()
    {
        await using var h = await EngineHarness.CreateAsync(50, s =>
        {
            s.MaxAutoRetries = 2;
            s.RetryIntervalMinutes = 1;
        });
        h.PrintEngine.FailBeforeAnyOutput = true;

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        h.Engine.StartJob(job.Id);

        for (var i = 0; i < 6; i++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(2));
            await Task.Delay(80);
        }

        (await h.WaitForStateAsync(JobState.NeedsAttention, 10_000)).Should().BeTrue();
        h.PrintEngine.SubmissionCount.Should().BeLessThanOrEqualTo(2,
            "the retry cap must hold even though every attempt fails");
    }

    [Fact]
    public async Task AFrozenPageCounterContradictingWindowsStopsForAHuman()
    {
        // Windows reports the job as printed, but the printer's physical counter never
        // moves. The two sources contradict each other, and the engine must refuse to pick
        // a winner rather than risk either a duplicate stack or a silently missing chunk.
        await using var h = await EngineHarness.CreateAsync(50);
        h.Printer.FreezeCounter = true;

        // Retain the finished job in the queue, as Microsoft Print to PDF and several real
        // drivers do, so Windows definitely reports the full page count and the printed
        // flag. That makes the contradiction with the frozen counter unambiguous.
        h.Printer.RetainFinishedJobs = true;

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        h.Engine.StartJob(job.Id);

        (await h.WaitForStateAsync(JobState.NeedsAttention, 25_000)).Should().BeTrue(h.Diagnostics());
        h.PrintEngine.SubmissionCount.Should().Be(1, "a contradiction must never trigger a resend");
        h.Job.AttentionReason.Should().Contain("cannot both be right");
    }

    [Fact]
    public async Task ACounterThatLagsBehindWindowsIsWaitedForRatherThanMisread()
    {
        // The HP reports its impression counter asynchronously. Reading it the instant the
        // queue empties would systematically under-count and manufacture false alarms.
        await using var h = await EngineHarness.CreateAsync(50);
        h.Printer.CounterLagAdvances = 4;

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        h.Engine.StartJob(job.Id);

        (await h.WaitForStateAsync(JobState.Completed, 25_000)).Should().BeTrue(
            "a lagging counter must be allowed to catch up before the batch is judged");
        h.Job.Batches[0].PagesConfirmedPrinted.Should().Be(50);
    }

    // ---- Pause, resume, cancel ------------------------------------------------

    [Fact]
    public async Task PauseStopsTheNextBatchFromStarting()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 30);

        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue();

        h.Engine.PauseJob(job.Id);
        (await h.WaitForStateAsync(JobState.Paused)).Should().BeTrue();

        h.SkipDelay(120);
        await Task.Delay(200);

        h.PrintEngine.SubmissionCount.Should().Be(1, "a paused job must not start its next batch");

        h.Engine.ResumeJob(job.Id);
        (await h.WaitForStateAsync(JobState.Completed, 20_000)).Should().BeTrue(h.Diagnostics());
        h.Printer.TotalPagesPrinted.Should().Be(100);
    }

    [Fact]
    public async Task CancellingStopsEverythingAndMarksRemainingBatchesCancelled()
    {
        await using var h = await EngineHarness.CreateAsync(200);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 30);

        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue();

        h.Engine.CancelJob(job.Id);
        (await h.WaitForStateAsync(JobState.Cancelled)).Should().BeTrue();

        h.SkipDelay(120);
        await Task.Delay(200);

        h.PrintEngine.SubmissionCount.Should().Be(1);
        h.Job.Batches.Where(b => b.Number > 1).Should().OnlyContain(b => b.State == BatchState.Cancelled);
    }

    [Fact]
    public async Task ConfirmingABatchPrintedSkipsItWithoutResending()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        h.PrintEngine.FailAfterPartialOutput = true;

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.NeedsAttention, 20_000)).Should().BeTrue();

        h.PrintEngine.FailAfterPartialOutput = false;
        h.Engine.ConfirmBatchPrinted(job.Id, 1);

        (await h.WaitForStateAsync(JobState.Completed, 20_000)).Should().BeTrue();
        h.Job.Batches.Single(b => b.Number == 1).State.Should().Be(BatchState.Skipped);
        h.PrintEngine.SubmissionCount.Should().Be(2, "only batch 2 should have been sent afterwards");
    }

    [Fact]
    public async Task ConfirmingABatchDidNotPrintResendsExactlyThatBatch()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        h.PrintEngine.FailAfterPartialOutput = true;

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.NeedsAttention, 20_000)).Should().BeTrue();

        h.PrintEngine.FailAfterPartialOutput = false;
        h.Printer.VanishJob(h.PrintEngine.SubmittedTokens[0]);
        h.Engine.ConfirmBatchNotPrinted(job.Id, 1);

        (await h.WaitForStateAsync(JobState.Completed, 20_000)).Should().BeTrue();
        h.Job.Batches.Single(b => b.Number == 1).State.Should().Be(BatchState.Completed);
    }

    // ---- Power management --------------------------------------------------------

    [Fact]
    public async Task TheMachineIsKeptAwakeWhileAJobIsRunningAndReleasedAfterwards()
    {
        await using var h = await EngineHarness.CreateAsync(50);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);

        h.Engine.StartJob(job.Id);
        (await h.WaitUntilAsync(() => h.Power.AcquireCount > 0)).Should().BeTrue();

        (await h.WaitForStateAsync(JobState.Completed, 15_000)).Should().BeTrue();
        (await h.WaitUntilAsync(() => !h.Power.IsHoldingRequest)).Should().BeTrue(
            "the keep-awake request must be released once there is nothing left to do");
    }

    // ---- Notifications --------------------------------------------------------------

    [Fact]
    public async Task TheUserIsToldAboutBatchCompletionsAndTheFinishedJobButNotSpammed()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 30);

        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue();
        h.SkipDelay(31);
        (await h.WaitForStateAsync(JobState.Completed, 20_000)).Should().BeTrue();

        h.Notifier.Notifications.Should().Contain(n => n.Title.Contains("Batch 1"));
        h.Notifier.Notifications.Should().Contain(n => n.Title.Contains("Printing finished"));
        h.Notifier.Notifications.Should().HaveCountLessThan(10, "an overnight job must not spam the user");
    }
}
