using FluentAssertions;
using PrintPacer.Core.Model;
using PrintPacer.Core.Persistence;
using Xunit;

namespace PrintPacer.Tests;

/// <summary>
/// What happens when the application stops at the worst possible moment.
///
/// These are the tests that matter most. The user's stated fear is waking up to a
/// duplicate stack of paper, or to a book with fifty pages missing from the middle. Every
/// test here restarts the engine from disk, exactly as reopening the application would.
/// </summary>
public class RecoveryTests
{
    [Fact]
    public async Task ClosingDuringTheRestPeriodResumesAtTheNextBatchNotTheFirst()
    {
        await using var h = await EngineHarness.CreateAsync(200);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 60);
        var jobId = job.Id;

        h.Engine.StartJob(jobId);
        (await h.WaitForBatchStateAsync(1, BatchState.Completed)).Should().BeTrue(h.Diagnostics());
        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue();

        await h.RestartApplicationAsync();

        (await h.JobsAsync()).Should().ContainSingle();
        h.Job.Batches.Single(b => b.Number == 1).State.Should().Be(BatchState.Completed,
            "a completed batch must stay completed across a restart");
        h.Job.Batches.Where(b => b.Number > 1).Should().OnlyContain(b => b.State == BatchState.Pending);

        h.LastRecovery.Should().ContainSingle();
        h.LastRecovery[0].NeedsUserDecision.Should().BeFalse(
            "nothing was in flight, so there is nothing to ask about");
        h.LastRecovery[0].Summary.Should().Contain("batch 2");

        // Finish the job and confirm no page is printed twice.
        for (var i = 0; i < 4; i++)
        {
            h.SkipDelay(61);
            await h.WaitUntilAsync(() => h.Job.State == JobState.Completed, 8_000);
            if (h.Job.State == JobState.Completed) break;
        }

        h.Job.State.Should().Be(JobState.Completed, h.Diagnostics());
        h.Printer.TotalPagesPrinted.Should().Be(200, "exactly 200 sheets, no duplicates");
    }

    [Fact]
    public async Task CrashingMidBatchNeverResendsThatBatchByItself()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 60);

        h.Engine.StartJob(job.Id);
        (await h.WaitUntilAsync(() =>
            h.Job.Batches[0].SpoolerPagesPrinted >= 10)).Should().BeTrue(h.Diagnostics());

        var pagesBefore = h.Printer.TotalPagesPrinted;
        var submissionsBefore = h.PrintEngine.SubmissionCount;

        // Power cut. The spooler keeps the job; the application loses everything in memory.
        await h.RestartApplicationAsync(graceful: false);

        // Whatever the engine concludes, it must not have created a second print job.
        await Task.Delay(500);
        h.PrintEngine.SubmissionCount.Should().Be(0,
            "the restarted process must not resend a batch that may already be printing");
        h.Printer.TotalPagesPrinted.Should().BeGreaterThanOrEqualTo(pagesBefore);
        submissionsBefore.Should().Be(1);
    }

    [Fact]
    public async Task AJobStillInTheQueueAfterARestartIsAdoptedNotDuplicated()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        h.Printer.PagesPerAdvance = 1;   // keep the job in the queue during the restart

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 60);
        h.Engine.StartJob(job.Id);

        (await h.WaitUntilAsync(() =>
            h.Job.Batches[0].ObservedInQueue == true)).Should().BeTrue(h.Diagnostics());

        var token = h.PrintEngine.SubmittedTokens[0];
        h.Printer.Snapshot().Should().Contain(j => j.Document == token);

        await h.RestartApplicationAsync(graceful: false);

        h.LastRecovery.Should().ContainSingle();
        h.LastRecovery[0].NeedsUserDecision.Should().BeTrue();
        h.LastRecovery[0].Details.Should().ContainMatch("*still in the Windows print queue*");

        h.Job.State.Should().Be(JobState.NeedsAttention);
        h.PrintEngine.SubmissionCount.Should().Be(0, "the surviving job must be adopted, not duplicated");
    }

    [Fact]
    public async Task ABatchThatFinishedWhileTheApplicationWasDownIsRecognisedFromThePageCounter()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        h.Printer.PagesPerAdvance = 1;

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 60);
        h.Engine.StartJob(job.Id);

        (await h.WaitUntilAsync(() => h.Job.Batches[0].ObservedInQueue == true,
            5_000, advancePrinter: false)).Should().BeTrue(h.Diagnostics());

        await h.RestartApplicationAsync(graceful: false);
        // At this point recovery saw the job still queued and asked for a decision.
        h.Job.Batches[0].State.Should().Be(BatchState.NeedsAttention);

        // Now let the printer finish the batch while the application is closed again.
        h.Printer.PagesPerAdvance = 50;
        h.Printer.RunToIdle();
        h.Printer.TotalPagesPrinted.Should().Be(50);

        await h.RestartApplicationAsync(graceful: false);

        // The physical counter advanced by exactly the batch size, so the engine can tell
        // the batch printed without any human input.
        var batch = h.Job.Batches.Single(b => b.Number == 1);
        batch.State.Should().Be(BatchState.Completed, h.Diagnostics());
        batch.LatestAttemptEvidence.Should().HaveFlag(CompletionEvidence.DeviceCounterMatched);

        // Batch 1 is not resent, and batch 2 does not start immediately either: the printer
        // has just produced fifty sheets, so it still gets its configured rest first.
        h.PrintEngine.SubmissionCount.Should().Be(0, h.Diagnostics());
        (await h.WaitForStateAsync(JobState.CoolingDown, 5_000)).Should().BeTrue(h.Diagnostics());
        h.PrintEngine.SubmissionCount.Should().Be(0);
    }

    [Fact]
    public async Task ABatchThatNeverPrintedWhileTheApplicationWasDownIsSafelyResent()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        h.Printer.PagesPerAdvance = 1;

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 60);
        h.Engine.StartJob(job.Id);

        // Do not advance the printer: the job reaches the queue but never marks any paper.
        (await h.WaitUntilAsync(() => h.Job.Batches[0].ObservedInQueue == true,
            5_000, advancePrinter: false)).Should().BeTrue(h.Diagnostics());

        h.Printer.TotalPagesPrinted.Should().Be(0);
        h.Printer.VanishJob(h.PrintEngine.SubmittedTokens[0]);

        await h.RestartApplicationAsync(graceful: false);

        var batch = h.Job.Batches.Single(b => b.Number == 1);
        batch.LatestAttemptState.Should().Be(AttemptState.Failed, h.Diagnostics());
        batch.LatestAttemptEvidence.Should().HaveFlag(CompletionEvidence.DeviceCounterZero);

        // Safe to resend, and the engine does so on its own.
        (await h.WaitForStateAsync(JobState.CoolingDown, 10_000)).Should().BeTrue(h.Diagnostics());
    }

    [Fact]
    public async Task WithoutAPageCounterAnInterruptedBatchAlwaysAsksTheUser()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        h.Printer.PagesPerAdvance = 1;
        h.Printer.ImpressionCounter = null;   // a printer with no management interface

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 60);
        h.Engine.StartJob(job.Id);

        (await h.WaitUntilAsync(() =>
            h.Job.Batches[0].ObservedInQueue == true)).Should().BeTrue(h.Diagnostics());

        h.Printer.VanishJob(h.PrintEngine.SubmittedTokens[0]);
        await h.RestartApplicationAsync(graceful: false);

        h.LastRecovery[0].NeedsUserDecision.Should().BeTrue(
            "with no physical evidence the outcome is genuinely unknown and must not be guessed");
        h.Job.State.Should().Be(JobState.NeedsAttention);
        h.PrintEngine.SubmissionCount.Should().Be(0);
        h.Job.Batches[0].Note.Should().Contain("output tray");
    }

    [Fact]
    public async Task TheSubmitIntentIsOnDiskBeforeAnythingReachesTheSpooler()
    {
        // The central crash-safety invariant, asserted directly against the database.
        await using var h = await EngineHarness.CreateAsync(50);
        h.PrintEngine.SubmitDelay = TimeSpan.FromMilliseconds(400);

        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        h.Engine.StartJob(job.Id);

        // While the engine is still inside SubmitAsync, a completely separate reader must
        // already be able to see the intent.
        (await h.WaitUntilAsync(() => h.PrintEngine.SubmissionCount == 1, 5_000,
            advancePrinter: false)).Should().BeTrue();

        using var independentReader = new JobStore(Path.Combine(h.Root, "jobs.db"));
        var persisted = independentReader.LoadAllJobs().Single();
        var attempt = persisted.Batches.Single(b => b.Number == 1).Attempts.SingleOrDefault();

        attempt.Should().NotBeNull(
            "the intent to print must be durable before the print API is ever called");
        attempt!.DocumentToken.Should().Be(h.PrintEngine.SubmittedTokens[0]);
        attempt.DocumentToken.Should().Contain("PrintPacer#");
    }

    [Fact]
    public async Task EveryAttemptGetsAUniqueDocumentTokenSoRecoveryCannotConfuseThem()
    {
        await using var h = await EngineHarness.CreateAsync(200);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);

        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.Completed, 30_000)).Should().BeTrue(h.Diagnostics());

        h.PrintEngine.SubmittedTokens.Should().HaveCount(4);
        h.PrintEngine.SubmittedTokens.Should().OnlyHaveUniqueItems();
        h.PrintEngine.SubmittedTokens.Should().OnlyContain(t => JobStore.IsOurDocument(t));
    }

    [Fact]
    public async Task ADocumentChangedWhileTheJobWasPausedStopsRatherThanPrintingWrongPages()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 30);

        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue(h.Diagnostics());

        // Someone replaces the PDF with a different document.
        PrintPacer.Core.Simulation.TestPdfBuilder.Write(h.PdfPath, 100, "A completely different document");

        h.SkipDelay(31);

        (await h.WaitForStateAsync(JobState.NeedsAttention, 10_000)).Should().BeTrue(h.Diagnostics());
        h.Job.AttentionReason.Should().Contain("changed");
        h.PrintEngine.SubmissionCount.Should().Be(1, "page 51 of a different document is not page 51");
    }

    [Fact]
    public async Task AMissingSourceFileStopsTheJobWithAClearMessage()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 30);

        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue(h.Diagnostics());

        File.Delete(h.PdfPath);
        h.SkipDelay(31);

        (await h.WaitForStateAsync(JobState.NeedsAttention, 10_000)).Should().BeTrue(h.Diagnostics());
        h.Job.AttentionReason.Should().Contain("no longer at");
    }

    [Fact]
    public async Task TheEventLogTellsTheStoryOfTheNight()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        var job = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 60);

        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue();
        h.SkipDelay(61);
        (await h.WaitForStateAsync(JobState.Completed, 15_000)).Should().BeTrue(h.Diagnostics());

        var messages = h.Events().Select(e => e.Message).ToList();

        messages.Should().ContainMatch("Job created*");
        messages.Should().ContainMatch("Batch 1 of 2 submitted*");
        messages.Should().ContainMatch("Batch 1 completed*");
        messages.Should().ContainMatch("Resting the printer*");
        messages.Should().ContainMatch("Batch 2 of 2 submitted*");
        messages.Should().ContainMatch("Job complete*");

        h.Events().Should().BeInAscendingOrder(e => e.Utc);
    }
}
