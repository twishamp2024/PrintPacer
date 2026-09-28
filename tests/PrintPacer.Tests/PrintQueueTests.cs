using FluentAssertions;
using Microsoft.Data.Sqlite;
using PrintPacer.Core.Billing;
using PrintPacer.Core.Engine;
using PrintPacer.Core.Model;
using PrintPacer.Core.Pdf;
using PrintPacer.Core.Persistence;
using PrintPacer.Core.Printing;
using Xunit;

namespace PrintPacer.Tests;

/// <summary>
/// Many customers' PDFs, each its own job with its own settings, printed one after another
/// from one queue - overnight, through failures and restarts.
///
/// Like the other engine tests these drive the real engine and the real SQLite store against
/// a simulated printer and clock.
/// </summary>
public class PrintQueueTests
{
    // ---- Every file its own job ------------------------------------------------------

    [Fact]
    public async Task EveryDocumentBecomesItsOwnJobWithItsOwnNumberAndPlace()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var documents = new[]
        {
            h.AddDocument(84, "customer1.pdf"), h.AddDocument(21, "customer2.pdf"), h.AddDocument(37, "customer3.pdf"),
            h.AddDocument(6, "customer4.pdf"), h.AddDocument(12, "customer5.pdf"),
        };

        var jobs = new List<JobSnapshot>();
        foreach (var document in documents)
            jobs.Add(await h.CreateJobAsync(document, h.Config(pagesPerBatch: 10, delayMinutes: 5)));

        jobs.Select(j => j.Id).Should().OnlyHaveUniqueItems();
        jobs.Select(j => j.Number).Should().Equal(1, 2, 3, 4, 5);
        jobs.Select(j => j.QueuePosition).Should().BeInAscendingOrder();
        jobs.Select(j => j.State).Should().OnlyContain(s => s == JobState.Draft, "nothing prints until the queue is started");

        // Nothing merged: each job is exactly its own document.
        jobs.Select(j => j.SourcePath).Should().Equal(documents.Select(d => d.Path));
        jobs.Select(j => j.TotalPages).Should().Equal(84, 21, 37, 6, 12);

        (await h.JobsAsync()).Select(j => j.FileName).Should().Equal(
            "customer1.pdf", "customer2.pdf", "customer3.pdf", "customer4.pdf", "customer5.pdf");
    }

    [Fact]
    public async Task NewJobsStartFromTheDefaultsButEachKeepsItsOwnCopy()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var defaults = new AppSettings
        {
            DefaultPagesPerBatch = 30, DefaultDelayMinutes = 20, ColourMode = ColourMode.BlackAndWhite,
            DefaultDuplex = DuplexMode.LongEdge, RenderDpi = 200,
        };

        var a = await h.CreateJobAsync(h.AddDocument(60, "a.pdf"), JobConfiguration.FromDefaults(defaults, h.Printer.Name));
        var b = await h.CreateJobAsync(h.AddDocument(60, "b.pdf"), JobConfiguration.FromDefaults(defaults, h.Printer.Name));

        a.Configuration.Should().Be(b.Configuration, "both started from the same defaults");
        a.Configuration.ColourMode.Should().Be(ColourMode.BlackAndWhite);
        a.Configuration.PrintSettings.Duplex.Should().Be(DuplexMode.LongEdge);
        a.Configuration.PagesPerBatch.Should().Be(30);
        a.Configuration.RenderDpi.Should().Be(200);

        // Changing the defaults afterwards reaches no existing job...
        defaults.DefaultPagesPerBatch = 5;
        defaults.ColourMode = ColourMode.Colour;
        (await h.JobsAsync()).Should().OnlyContain(j => j.Configuration.PagesPerBatch == 30 &&
                                                        j.Configuration.ColourMode == ColourMode.BlackAndWhite);

        // ...and changing a job reaches neither the defaults nor the other job.
        await h.Engine.UpdateJobAsync(a.Id, a.Configuration with { PagesPerBatch = 12, DelayMinutes = 0 });
        defaults.DefaultPagesPerBatch.Should().Be(5);
        defaults.DefaultDelayMinutes.Should().Be(20);
        h.Snapshot(b.Id).Configuration.PagesPerBatch.Should().Be(30);
    }

    [Fact]
    public async Task ChangingOneJobChangesNoOtherJobNowOrAfterARestart()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var a = await h.CreateJobAsync(h.AddDocument(40, "a.pdf"), h.Config(pagesPerBatch: 20, delayMinutes: 10));
        var b = await h.CreateJobAsync(h.AddDocument(40, "b.pdf"), h.Config(pagesPerBatch: 20, delayMinutes: 10));
        var bBefore = h.Snapshot(b.Id);

        var startAt = h.Clock.UtcNow.AddHours(3);
        var changed = await h.Engine.UpdateJobAsync(a.Id, a.Configuration with
        {
            ColourMode = ColourMode.BlackAndWhite,
            PagesPerBatch = 15,
            DelayMinutes = 45,
            StartMode = StartMode.AtTime,
            ScheduledStartUtc = startAt,
            PrintSettings = PrintSettings.Default with { Duplex = DuplexMode.LongEdge },
        });

        changed.ColourMode.Should().Be(ColourMode.BlackAndWhite);
        changed.Unit.Should().Be(PrintUnit.Sheet, "double-sided batches count sheets");
        changed.BatchCount.Should().Be(2, "40 pages double-sided are 20 sheets, in batches of 15");

        h.Snapshot(b.Id).Configuration.Should().Be(bBefore.Configuration);
        h.Snapshot(b.Id).Batches.Should().BeEquivalentTo(bBefore.Batches);

        await h.RestartApplicationAsync();

        var jobs = (await h.JobsAsync()).ToDictionary(j => j.Id);
        jobs[a.Id].Configuration.Should().Be(changed.Configuration);
        jobs[a.Id].Configuration.ScheduledStartUtc.Should().Be(startAt);
        jobs[b.Id].Configuration.Should().Be(bBefore.Configuration);
    }

    [Fact]
    public async Task AJobThatHasStartedCanNoLongerBeChanged()
    {
        await using var h = await EngineHarness.CreateAsync(20);
        var job = await h.CreateJobAsync(pagesPerBatch: 10, delayMinutes: 60);
        h.Engine.StartJob(job.Id);
        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue(h.Diagnostics());

        var change = () => h.Engine.UpdateJobAsync(job.Id, h.Job.Configuration with { PagesPerBatch = 5 });

        await change.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already started*");
        h.Job.PagesPerBatch.Should().Be(10);
        h.Job.Batches.Should().HaveCount(2);
    }

    // ---- Each job prints with its own settings -----------------------------------------

    [Fact]
    public async Task EachJobPrintsWithItsOwnColourSidesAndBatchSize()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var one = h.AddDocument(40, "customer1.pdf");
        var two = h.AddDocument(60, "customer2.pdf");
        var three = h.AddDocument(25, "customer3.pdf");

        var j1 = await h.CreateJobAsync(one, h.Config(pagesPerBatch: 20, delayMinutes: 10));
        var j2 = await h.CreateJobAsync(two, h.Config(pagesPerBatch: 15, delayMinutes: 20, ColourMode.BlackAndWhite,
            PrintSettings.Default with { Duplex = DuplexMode.LongEdge }));
        var j3 = await h.CreateJobAsync(three, h.Config(pagesPerBatch: 10, delayMinutes: 5));

        h.Engine.StartQueue();
        (await h.RunQueueToEndAsync(new[] { j1.Id, j2.Id, j3.Id })).Should().BeTrue(h.Diagnostics());

        var requests = h.PrintEngine.SubmittedRequests;
        var first = requests.Where(r => r.SourcePath == one.Path).ToList();
        var second = requests.Where(r => r.SourcePath == two.Path).ToList();
        var third = requests.Where(r => r.SourcePath == three.Path).ToList();

        first.Select(r => (r.FirstPage, r.LastPage)).Should().Equal((1, 20), (21, 40));
        first.Should().OnlyContain(r => r.ColourMode == ColourMode.Colour && r.Setup.Duplex == DuplexMode.SingleSided);

        // 60 pages on 30 sheets, 15 sheets per batch.
        second.Select(r => (r.FirstPage, r.LastPage)).Should().Equal((1, 15), (16, 30));
        second.Should().OnlyContain(r => r.ColourMode == ColourMode.BlackAndWhite && r.Setup.Duplex == DuplexMode.LongEdge);

        third.Select(r => (r.FirstPage, r.LastPage)).Should().Equal((1, 10), (11, 20), (21, 25));
        third.Should().OnlyContain(r => r.ColourMode == ColourMode.Colour && r.Setup.Duplex == DuplexMode.SingleSided);

        h.Printer.TotalPagesPrinted.Should().Be(40 + 60 + 25, "every page exactly once");
        new[] { j1.Id, j2.Id, j3.Id }.Select(id => h.Snapshot(id).State).Should().OnlyContain(s => s == JobState.Completed);
    }

    [Fact]
    public async Task JobsRunOneAfterAnotherInQueueOrderAndNeverOverlap()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var docs = new[] { h.AddDocument(30, "a.pdf"), h.AddDocument(20, "b.pdf"), h.AddDocument(30, "c.pdf") };
        var ids = new List<string>();
        foreach (var doc in docs) ids.Add((await h.CreateJobAsync(doc, h.Config(pagesPerBatch: 10, delayMinutes: 15))).Id);

        h.Engine.StartQueue();

        // At every step, at most one job is doing anything with the printer.
        var overlapped = false;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && !ids.All(id => h.Snapshot(id).IsTerminal))
        {
            var busy = ids.Select(h.Snapshot).Count(j => j.State is not (JobState.Queued or JobState.Draft) && !j.IsTerminal);
            overlapped |= busy > 1;
            await h.WaitUntilAsync(() => ids.Any(id => h.Snapshot(id).State is JobState.CoolingDown) ||
                                         h.Engine.GetJobsAsync().Result.Any(j => j.LastPrinterStatus?.Contains("Starts at") == true), 1_000);
            h.SkipDelay(16);
        }

        overlapped.Should().BeFalse("only one job may have the printer at a time");
        ids.Select(h.Snapshot).Should().OnlyContain(j => j.State == JobState.Completed, h.Diagnostics());

        var order = h.PrintEngine.SubmittedRequests.Select(r => r.SourcePath).ToList();
        Collapse(order).Should().Equal(docs.Select(d => d.Path), "each job finishes before the next begins, in queue order");
    }

    [Fact]
    public async Task ThePrinterRestsBetweenBatchesAndBetweenJobs()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var a = await h.CreateJobAsync(h.AddDocument(20, "a.pdf"), h.Config(pagesPerBatch: 10, delayMinutes: 30));
        var b = await h.CreateJobAsync(h.AddDocument(10, "b.pdf"), h.Config(pagesPerBatch: 10, delayMinutes: 5));

        h.Engine.StartQueue();
        (await h.WaitForJobStateAsync(a.Id, JobState.CoolingDown)).Should().BeTrue(h.Diagnostics());
        h.SkipDelay(31);
        (await h.WaitForJobStateAsync(a.Id, JobState.Completed)).Should().BeTrue(h.Diagnostics());

        // Job b waits for job a's rest, even though it has nothing to do with a's settings otherwise.
        (await h.WaitUntilAsync(() => h.Snapshot(b.Id).LastPrinterStatus?.Contains("Starts at") == true)).Should().BeTrue();
        h.Clock.Advance(TimeSpan.FromMinutes(29));
        await Task.Delay(100);
        h.Snapshot(b.Id).State.Should().Be(JobState.Queued, "29 minutes is not yet the 30-minute rest");
        h.PrintEngine.SubmittedRequests.Should().NotContain(r => r.SourcePath == h.Snapshot(b.Id).SourcePath);

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        (await h.WaitForJobStateAsync(b.Id, JobState.Completed)).Should().BeTrue(h.Diagnostics());

        // The recorded times agree: every gap is at least the rest the finished job asked for.
        var aBatches = h.Snapshot(a.Id).Batches;
        var bBatches = h.Snapshot(b.Id).Batches;
        (aBatches[1].SubmittedUtc - aBatches[0].FinishedUtc).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(30));
        (bBatches[0].SubmittedUtc - aBatches[1].FinishedUtc).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task AClockThatMovesBackwardsCannotStretchTheRestBetweenJobs()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var a = await h.CreateJobAsync(h.AddDocument(10, "a.pdf"), h.Config(pagesPerBatch: 10, delayMinutes: 30));
        var b = await h.CreateJobAsync(h.AddDocument(10, "b.pdf"), h.Config(pagesPerBatch: 10, delayMinutes: 0));

        h.Engine.StartQueue();
        (await h.WaitForJobStateAsync(a.Id, JobState.Completed)).Should().BeTrue(h.Diagnostics());
        (await h.WaitUntilAsync(() => h.Snapshot(b.Id).LastPrinterStatus?.Contains("Starts at") == true)).Should().BeTrue();

        // Three hours back, and a restart: job a now seems to have finished in the future.
        h.Clock.JumpClock(TimeSpan.FromHours(-3));
        await h.RestartApplicationAsync();
        h.Snapshot(b.Id).State.Should().Be(JobState.Queued);

        h.Clock.Advance(TimeSpan.FromMinutes(31));
        (await h.WaitForJobStateAsync(b.Id, JobState.Completed)).Should().BeTrue(
            "the rest is 30 minutes however the clock was set, not 3 hours and 30 minutes" + h.Diagnostics());
    }

    [Fact]
    public async Task AJobOnAnotherPrinterNeedsNoRestFromThePreviousJob()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var a = await h.CreateJobAsync(h.AddDocument(10, "a.pdf"), h.Config(pagesPerBatch: 10, delayMinutes: 60));
        var b = await h.CreateJobAsync(h.AddDocument(10, "b.pdf"), h.Config(pagesPerBatch: 10, delayMinutes: 60) with
        {
            PrinterName = "Office Laser",
        });

        h.Engine.StartQueue();
        (await h.WaitForJobStateAsync(a.Id, JobState.Completed)).Should().BeTrue(h.Diagnostics());

        // No rest to wait for: b goes straight to its own printer, which is not installed here -
        // proving the job uses its own printer, not the one the previous job used.
        (await h.WaitUntilAsync(() => h.Snapshot(b.Id).State is JobState.BlockedPrinterError)).Should().BeTrue(h.Diagnostics());
        h.Snapshot(b.Id).LastPrinterStatus.Should().Contain("Office Laser");
        h.PrintEngine.SubmittedRequests.Should().OnlyContain(r => r.PrinterName == h.Printer.Name);

        await h.RestartApplicationAsync();
        (await h.JobsAsync()).Single(j => j.Id == b.Id).PrinterName.Should().Be("Office Laser");
        (await h.JobsAsync()).Single(j => j.Id == a.Id).PrinterName.Should().Be(h.Printer.Name);
    }

    [Fact]
    public async Task AScheduledJobHoldsItsPlaceUntilItsTimeAndTheNextJobFollowsIt()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var at = h.Clock.UtcNow.AddHours(3);
        var a = await h.CreateJobAsync(h.AddDocument(10, "night.pdf"), h.Config(pagesPerBatch: 10, delayMinutes: 10) with
        {
            StartMode = StartMode.AtTime,
            ScheduledStartUtc = at,
        });
        var b = await h.CreateJobAsync(h.AddDocument(10, "after.pdf"), h.Config(pagesPerBatch: 10, delayMinutes: 10));

        h.Engine.StartQueue();
        (await h.WaitForJobStateAsync(a.Id, JobState.WaitingForStart)).Should().BeTrue(h.Diagnostics());
        await Task.Delay(100);
        h.Snapshot(b.Id).State.Should().Be(JobState.Queued, "the next job starts after the scheduled one, not before it");
        h.PrintEngine.SubmissionCount.Should().Be(0);

        h.Clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));
        (await h.WaitForJobStateAsync(a.Id, JobState.Completed)).Should().BeTrue(h.Diagnostics());
        h.SkipDelay(11);
        (await h.WaitForJobStateAsync(b.Id, JobState.Completed)).Should().BeTrue(h.Diagnostics());

        h.Snapshot(a.Id).Batches[0].SubmittedUtc.Should().BeOnOrAfter(at);
        Collapse(h.PrintEngine.SubmittedRequests.Select(r => r.SourcePath).ToList())
            .Should().Equal(h.Snapshot(a.Id).SourcePath, h.Snapshot(b.Id).SourcePath);
    }

    // ---- Order ------------------------------------------------------------------------

    [Fact]
    public async Task ReorderingDecidesWhatPrintsFirstAndSurvivesARestart()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var rahul = await h.CreateJobAsync(h.AddDocument(10, "Rahul.pdf"), h.Config(10, 0));
        var priya = await h.CreateJobAsync(h.AddDocument(10, "Priya.pdf"), h.Config(10, 0));
        var amit = await h.CreateJobAsync(h.AddDocument(10, "Amit.pdf"), h.Config(10, 0));

        h.Engine.MoveJob(amit.Id, -2);
        await h.WaitUntilAsync(() => h.Snapshot(amit.Id).QueuePosition < h.Snapshot(rahul.Id).QueuePosition);

        await h.RestartApplicationAsync();
        (await h.JobsAsync()).Select(j => j.FileName).Should().Equal("Amit.pdf", "Rahul.pdf", "Priya.pdf");

        h.Engine.MoveJob(rahul.Id, +1);
        await h.WaitUntilAsync(() => h.Snapshot(rahul.Id).QueuePosition > h.Snapshot(priya.Id).QueuePosition);
        (await h.JobsAsync()).Select(j => j.FileName).Should().Equal("Amit.pdf", "Priya.pdf", "Rahul.pdf");

        h.Engine.StartQueue();
        (await h.RunQueueToEndAsync(new[] { rahul.Id, priya.Id, amit.Id })).Should().BeTrue(h.Diagnostics());

        Collapse(h.PrintEngine.SubmittedRequests.Select(r => Path.GetFileName(r.SourcePath)).ToList())
            .Should().Equal("Amit.pdf", "Priya.pdf", "Rahul.pdf");
    }

    [Fact]
    public async Task OnlyJobsThatHaveNotStartedMoveAndTheRunningJobKeepsItsPlace()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var a = await h.CreateJobAsync(h.AddDocument(20, "a.pdf"), h.Config(10, 60));
        var b = await h.CreateJobAsync(h.AddDocument(10, "b.pdf"), h.Config(10, 0));
        var c = await h.CreateJobAsync(h.AddDocument(10, "c.pdf"), h.Config(10, 0));

        h.Engine.StartQueue();
        (await h.WaitForJobStateAsync(a.Id, JobState.CoolingDown)).Should().BeTrue(h.Diagnostics());

        h.Engine.MoveJob(a.Id, +2);                       // started: stays first
        h.Engine.MoveJob(c.Id, -5);                       // clamps to the front of the waiting jobs
        await h.WaitUntilAsync(() => h.Snapshot(c.Id).QueuePosition < h.Snapshot(b.Id).QueuePosition);

        (await h.JobsAsync()).Select(j => j.FileName).Should().Equal("a.pdf", "c.pdf", "b.pdf");
        h.Snapshot(a.Id).State.Should().Be(JobState.CoolingDown, "moving others never disturbs the running job");
    }

    [Fact]
    public async Task AHeldBackJobStaysOutOfTheQueueUntilStarted()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var a = await h.CreateJobAsync(h.AddDocument(20, "a.pdf"), h.Config(10, 30));
        var b = await h.CreateJobAsync(h.AddDocument(10, "b.pdf"), h.Config(10, 0));

        h.Engine.StartQueue();
        (await h.WaitForJobStateAsync(a.Id, JobState.CoolingDown)).Should().BeTrue();
        h.Engine.HoldJob(b.Id);
        (await h.WaitForJobStateAsync(b.Id, JobState.Draft)).Should().BeTrue();

        h.SkipDelay(31);
        (await h.WaitForJobStateAsync(a.Id, JobState.Completed)).Should().BeTrue(h.Diagnostics());
        h.SkipDelay(31);
        await h.WaitUntilAsync(() => false, 300);
        h.Snapshot(b.Id).State.Should().Be(JobState.Draft);
        h.PrintEngine.SubmittedRequests.Should().NotContain(r => r.SourcePath == h.Snapshot(b.Id).SourcePath);

        h.Engine.StartJob(b.Id);
        (await h.WaitForJobStateAsync(b.Id, JobState.Completed)).Should().BeTrue(h.Diagnostics());
    }

    // ---- Removing and cancelling ------------------------------------------------------------

    [Fact]
    public async Task RemovingAWaitingJobLeavesTheOthersUntouchedAndARunningJobCannotBeRemoved()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var a = await h.CreateJobAsync(h.AddDocument(20, "a.pdf"), h.Config(10, 60));
        var b = await h.CreateJobAsync(h.AddDocument(10, "b.pdf"), h.Config(10, 0));
        var c = await h.CreateJobAsync(h.AddDocument(10, "c.pdf"), h.Config(5, 0, ColourMode.BlackAndWhite));
        var cBefore = h.Snapshot(c.Id);

        h.Engine.StartQueue();
        (await h.WaitForJobStateAsync(a.Id, JobState.CoolingDown)).Should().BeTrue(h.Diagnostics());

        (await h.Engine.RemoveJobAsync(a.Id)).Should().Contain("Cancel", "a job under way must be cancelled first");
        (await h.Engine.RemoveJobAsync(b.Id)).Should().BeNull();
        (await h.IsGoneAsync(b.Id)).Should().BeTrue();

        await h.RestartApplicationAsync();
        var jobs = await h.JobsAsync();
        jobs.Select(j => j.FileName).Should().Equal("a.pdf", "c.pdf");
        jobs.Single(j => j.Id == c.Id).Configuration.Should().Be(cBefore.Configuration);
        jobs.Single(j => j.Id == a.Id).Batches[0].State.Should().Be(BatchState.Completed, "a's records are intact");

        (await h.RunQueueToEndAsync(new[] { a.Id, c.Id })).Should().BeTrue(h.Diagnostics());
        h.PrintEngine.SubmittedRequests.Should().NotContain(r => r.SourcePath.EndsWith("b.pdf"));
    }

    [Fact]
    public async Task CancellingTheRunningJobLetsTheQueueMoveOn()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var a = await h.CreateJobAsync(h.AddDocument(30, "a.pdf"), h.Config(10, 20));
        var b = await h.CreateJobAsync(h.AddDocument(10, "b.pdf"), h.Config(10, 0));

        h.Engine.StartQueue();
        (await h.WaitForJobStateAsync(a.Id, JobState.CoolingDown)).Should().BeTrue(h.Diagnostics());

        h.Engine.CancelJob(a.Id);
        (await h.WaitForJobStateAsync(a.Id, JobState.Cancelled)).Should().BeTrue();
        h.Snapshot(a.Id).Batches.Select(x => x.State).Should().Equal(BatchState.Completed, BatchState.Cancelled, BatchState.Cancelled);

        // The printer still gets its rest after a's first batch before b starts.
        h.SkipDelay(21);
        (await h.WaitForJobStateAsync(b.Id, JobState.Completed)).Should().BeTrue(h.Diagnostics());
        h.Printer.TotalPagesPrinted.Should().Be(10 + 10);

        // A finished job can then be cleared from the list; its history is not needed to print anything.
        (await h.Engine.RemoveJobAsync(a.Id)).Should().BeNull();
    }

    // ---- Failure isolation ---------------------------------------------------------------

    [Fact]
    public async Task AJobThatFailsDoesNotHoldBackOrDamageTheJobsAfterIt()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var docs = new[] { h.AddDocument(10, "one.pdf"), h.AddDocument(20, "two.pdf"), h.AddDocument(10, "three.pdf"), h.AddDocument(10, "four.pdf") };
        var ids = new List<string>();
        foreach (var doc in docs) ids.Add((await h.CreateJobAsync(doc, h.Config(10, 0))).Id);
        var twoBefore = h.Snapshot(ids[1]);

        // Customer two's file disappears before its turn.
        var parked = docs[1].Path + ".away";
        File.Move(docs[1].Path, parked);

        h.Engine.StartQueue();
        (await h.WaitUntilAsync(() => h.Snapshot(ids[3]).State == JobState.Completed, 20_000)).Should().BeTrue(h.Diagnostics());

        h.Snapshot(ids[0]).State.Should().Be(JobState.Completed);
        h.Snapshot(ids[2]).State.Should().Be(JobState.Completed);

        var two = h.Snapshot(ids[1]);
        two.State.Should().Be(JobState.NeedsAttention);
        two.AttentionReason.Should().Contain("no longer at");
        two.Batches.Should().OnlyContain(x => x.State == BatchState.Pending, "nothing of it was lost or skipped");
        two.Configuration.Should().Be(twoBefore.Configuration);
        h.Events(ids[1]).Should().Contain(e => e.Message.Contains("the queue carries on"));

        // Put the file back and carry on: job two prints in full, once.
        File.Move(parked, docs[1].Path);
        h.Engine.ResumeJob(ids[1]);
        (await h.WaitForJobStateAsync(ids[1], JobState.Completed, 20_000)).Should().BeTrue(h.Diagnostics());
        h.Printer.TotalPagesPrinted.Should().Be(10 + 20 + 10 + 10);
    }

    [Fact]
    public async Task WithCarryOnSwitchedOffAJobThatNeedsADecisionHoldsTheQueue()
    {
        await using var h = await EngineHarness.CreateAsync(10, s => s.ContinueQueueWhenJobNeedsDecision = false);
        var one = h.AddDocument(10, "one.pdf");
        var two = h.AddDocument(10, "two.pdf");
        var a = await h.CreateJobAsync(one, h.Config(10, 0));
        var b = await h.CreateJobAsync(two, h.Config(10, 0));
        File.Move(one.Path, one.Path + ".away");

        h.Engine.StartQueue();
        (await h.WaitForJobStateAsync(a.Id, JobState.NeedsAttention)).Should().BeTrue(h.Diagnostics());
        await h.WaitUntilAsync(() => false, 300);
        h.Snapshot(b.Id).State.Should().Be(JobState.Queued);
        h.Snapshot(b.Id).LastPrinterStatus.Should().Contain("needs your decision");
        h.PrintEngine.SubmissionCount.Should().Be(0);

        File.Move(one.Path + ".away", one.Path);
        h.Engine.ResumeJob(a.Id);
        (await h.WaitForJobStateAsync(b.Id, JobState.Completed)).Should().BeTrue(h.Diagnostics());
        Collapse(h.PrintEngine.SubmittedRequests.Select(r => r.SourcePath).ToList()).Should().Equal(one.Path, two.Path);
    }

    [Fact]
    public async Task AJobResolvedWhileAnotherIsPrintingWaitsForItsTurn()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var one = h.AddDocument(10, "one.pdf");
        var two = h.AddDocument(20, "two.pdf");
        var three = h.AddDocument(10, "three.pdf");
        var a = await h.CreateJobAsync(one, h.Config(10, 0));
        var b = await h.CreateJobAsync(two, h.Config(10, 30));
        var c = await h.CreateJobAsync(three, h.Config(10, 0));
        File.Move(one.Path, one.Path + ".away");

        h.Engine.StartQueue();
        (await h.WaitForJobStateAsync(b.Id, JobState.CoolingDown)).Should().BeTrue(h.Diagnostics());
        h.Snapshot(a.Id).State.Should().Be(JobState.NeedsAttention);

        // Fixed while b rests between its batches: a must not print in the middle of b.
        File.Move(one.Path + ".away", one.Path);
        h.Engine.ResumeJob(a.Id);
        (await h.WaitForJobStateAsync(a.Id, JobState.Queued)).Should().BeTrue(h.Diagnostics());
        h.PrintEngine.SubmittedRequests.Should().NotContain(r => r.SourcePath == one.Path);

        (await h.RunQueueToEndAsync(new[] { a.Id, b.Id, c.Id })).Should().BeTrue(h.Diagnostics());

        // b finished, then a (its place is before c), then c.
        Collapse(h.PrintEngine.SubmittedRequests.Select(r => r.SourcePath).ToList())
            .Should().Equal(two.Path, one.Path, three.Path);
        h.Printer.TotalPagesPrinted.Should().Be(40);
    }

    [Fact]
    public async Task AJobResumedByHandStillLetsThePrinterRestFromTheJobBeforeIt()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var one = h.AddDocument(10, "one.pdf");
        var two = h.AddDocument(10, "two.pdf");
        var a = await h.CreateJobAsync(one, h.Config(10, 0));
        var b = await h.CreateJobAsync(two, h.Config(10, 30));
        File.Move(one.Path, one.Path + ".away");

        h.Engine.StartQueue();
        (await h.WaitForJobStateAsync(b.Id, JobState.Completed)).Should().BeTrue(h.Diagnostics());
        h.Snapshot(a.Id).State.Should().Be(JobState.NeedsAttention);

        // Fixed straight after b's last batch: a waits out b's 30-minute rest before printing.
        File.Move(one.Path + ".away", one.Path);
        h.Engine.ResumeJob(a.Id);
        (await h.WaitForJobStateAsync(a.Id, JobState.Queued)).Should().BeTrue(h.Diagnostics());
        h.Snapshot(a.Id).LastPrinterStatus.Should().Contain("rests 30 min from Job #2");
        h.PrintEngine.SubmittedRequests.Should().NotContain(r => r.SourcePath == one.Path);

        h.Clock.Advance(TimeSpan.FromMinutes(31));
        (await h.WaitForJobStateAsync(a.Id, JobState.Completed)).Should().BeTrue(h.Diagnostics());
        (h.Snapshot(a.Id).Batches[0].SubmittedUtc - h.Snapshot(b.Id).Batches[0].FinishedUtc)
            .Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task PagesThatMayStillBeInTheWindowsQueueHoldTheQueueUntilTheyAreGone()
    {
        await using var h = await EngineHarness.CreateAsync(100);
        h.Printer.PagesPerAdvance = 1;   // keep the batch in the Windows queue across the crash
        var next = h.AddDocument(10, "next.pdf");

        var a = await h.CreateJobAsync(pagesPerBatch: 50, delayMinutes: 0);
        var b = await h.CreateJobAsync(next, h.Config(10, 0));
        h.Engine.StartQueue();
        (await h.WaitUntilAsync(() => h.Snapshot(a.Id).Batches[0].ObservedInQueue)).Should().BeTrue(h.Diagnostics());

        await h.RestartApplicationAsync(graceful: false);
        h.Snapshot(a.Id).State.Should().Be(JobState.NeedsAttention, "the batch was adopted, not resent");
        h.LastRecovery.Should().ContainSingle(r => r.JobId == a.Id);

        // Its pages may still be printing, so b must not start - not even with carry-on switched on.
        await h.WaitUntilAsync(() => false, 400, advancePrinter: false);
        h.Snapshot(b.Id).State.Should().Be(JobState.Queued);
        h.PrintEngine.SubmissionCount.Should().Be(0);

        // Once that print job has left the Windows queue, the queue carries on without it.
        h.Printer.PagesPerAdvance = 50;
        h.Printer.RunToIdle();
        (await h.WaitForJobStateAsync(b.Id, JobState.Completed)).Should().BeTrue(h.Diagnostics());
        h.Snapshot(a.Id).State.Should().Be(JobState.NeedsAttention, "a still waits for someone to check its pages");
        h.PrintEngine.SubmittedRequests.Should().OnlyContain(r => r.SourcePath == next.Path, "a's batch is never resent by itself");
    }

    // ---- Restarts and crashes ------------------------------------------------------------------

    [Fact]
    public async Task TheWholeQueueSurvivesARestart()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var a = await h.CreateJobAsync(h.AddDocument(20, "a.pdf"), h.Config(10, 60));
        var b = await h.CreateJobAsync(h.AddDocument(30, "b.pdf"), h.Config(15, 10, ColourMode.BlackAndWhite));
        var c = await h.CreateJobAsync(h.AddDocument(12, "c.pdf"), h.Config(4, 5));
        var d = await h.CreateJobAsync(h.AddDocument(8, "d.pdf"), h.Config(8, 0));   // left as a draft

        h.Engine.StartQueue(new[] { a.Id, b.Id, c.Id });
        (await h.WaitForJobStateAsync(a.Id, JobState.CoolingDown)).Should().BeTrue(h.Diagnostics());
        var before = (await h.JobsAsync()).ToDictionary(j => j.Id);

        await h.RestartApplicationAsync();

        var after = (await h.JobsAsync()).ToDictionary(j => j.Id);
        after.Keys.Should().BeEquivalentTo(before.Keys);
        foreach (var id in before.Keys)
        {
            after[id].Number.Should().Be(before[id].Number);
            after[id].QueuePosition.Should().Be(before[id].QueuePosition);
            after[id].Configuration.Should().Be(before[id].Configuration);
            after[id].Batches.Select(x => (x.FirstPage, x.LastPage, x.State))
                .Should().Equal(before[id].Batches.Select(x => (x.FirstPage, x.LastPage, x.State)));
        }

        after[a.Id].State.Should().Be(JobState.CoolingDown);
        after[b.Id].State.Should().Be(JobState.Queued);
        after[c.Id].State.Should().Be(JobState.Queued);
        after[d.Id].State.Should().Be(JobState.Draft);
        h.LastRecovery.Should().ContainSingle(r => r.JobId == a.Id, "only the job that had started had anything to recover");

        (await h.RunQueueToEndAsync(new[] { a.Id, b.Id, c.Id })).Should().BeTrue(h.Diagnostics());
        h.Printer.TotalPagesPrinted.Should().Be(20 + 30 + 12);
        h.Snapshot(d.Id).State.Should().Be(JobState.Draft, "a draft is never printed by a restart");
    }

    [Fact]
    public async Task ACrashOvernightNeitherLosesWaitingJobsNorPrintsFinishedOnesAgain()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var one = h.AddDocument(10, "one.pdf");
        var two = h.AddDocument(20, "two.pdf");
        var three = h.AddDocument(10, "three.pdf");
        var a = await h.CreateJobAsync(one, h.Config(10, 0));
        var b = await h.CreateJobAsync(two, h.Config(10, 60));
        var c = await h.CreateJobAsync(three, h.Config(10, 0));

        h.Engine.StartQueue();
        (await h.WaitForJobStateAsync(a.Id, JobState.Completed)).Should().BeTrue(h.Diagnostics());
        (await h.WaitForJobStateAsync(b.Id, JobState.CoolingDown)).Should().BeTrue(h.Diagnostics());

        // Power cut during b's rest.
        await h.RestartApplicationAsync(graceful: false);

        h.Snapshot(a.Id).State.Should().Be(JobState.Completed);
        h.Snapshot(b.Id).State.Should().Be(JobState.CoolingDown);
        h.Snapshot(b.Id).Batches.Select(x => x.State).Should().Equal(BatchState.Completed, BatchState.Pending);
        h.Snapshot(c.Id).State.Should().Be(JobState.Queued);
        h.LastRecovery.Should().ContainSingle(r => r.JobId == b.Id && !r.NeedsUserDecision);

        (await h.RunQueueToEndAsync(new[] { a.Id, b.Id, c.Id })).Should().BeTrue(h.Diagnostics());

        // After the restart: b's second batch, then c. Nothing of a, nothing of b's first batch.
        h.PrintEngine.SubmittedRequests.Select(r => (Path.GetFileName(r.SourcePath), r.FirstPage))
            .Should().Equal(("two.pdf", 11), ("three.pdf", 1));
        h.Printer.TotalPagesPrinted.Should().Be(40, "every page of every job exactly once");
    }

    // ---- The whole night -----------------------------------------------------------------

    [Fact]
    public async Task FiveCustomersFilesPrintOvernightEachWithItsOwnSettings()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var docs = new[]
        {
            h.AddDocument(40, "customer1.pdf"), h.AddDocument(60, "customer2.pdf"), h.AddDocument(25, "customer3.pdf"),
            h.AddDocument(16, "customer4.pdf"), h.AddDocument(11, "customer5.pdf"),
        };
        var configs = new[]
        {
            h.Config(20, 10),                                                                        // colour, single-sided, 20 pages/batch
            h.Config(15, 20, ColourMode.BlackAndWhite, PrintSettings.Default with { Duplex = DuplexMode.LongEdge }), // B&W, double-sided
            h.Config(10, 5),                                                                         // colour, 10 pages/batch
            h.Config(2, 15, ColourMode.BlackAndWhite, PrintSettings.Default with
            {
                Layout = PrintLayout.Booklet, Duplex = DuplexMode.ShortEdge, Paper = PaperChoice.A4,
            }),                                                                                      // B&W booklet
            h.Config(3, 0, printSettings: PrintSettings.Default with { PageSelection = PageSelection.OddPages }),
        };

        var ids = new List<string>();
        for (var i = 0; i < docs.Length; i++) ids.Add((await h.CreateJobAsync(docs[i], configs[i])).Id);

        h.Engine.StartQueue();
        (await h.RunQueueToEndAsync(ids, 120_000)).Should().BeTrue(h.Diagnostics());

        var jobs = ids.Select(h.Snapshot).ToList();
        jobs.Should().OnlyContain(j => j.State == JobState.Completed);

        // One job at a time, in queue order.
        Collapse(h.PrintEngine.SubmittedRequests.Select(r => r.SourcePath).ToList()).Should().Equal(docs.Select(d => d.Path));

        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            var requests = h.PrintEngine.SubmittedRequests.Where(r => r.SourcePath == docs[i].Path).ToList();

            requests.Should().HaveCount(job.BatchCount, $"{job.FileName} is batched by its own batch size");
            requests.Should().OnlyContain(r => r.ColourMode == configs[i].ColourMode && r.Setup.Duplex == configs[i].PrintSettings.Duplex);
            job.Batches.Should().OnlyContain(x => x.PageCount <= configs[i].PagesPerBatch);

            // Its own rest between its own batches.
            for (var n = 1; n < job.Batches.Count; n++)
                (job.Batches[n].SubmittedUtc - job.Batches[n - 1].FinishedUtc)
                    .Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(configs[i].DelayMinutes));

            // And the previous job's rest before its first batch.
            if (i > 0)
                (job.Batches[0].SubmittedUtc - jobs[i - 1].Batches[^1].FinishedUtc)
                    .Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(configs[i - 1].DelayMinutes));
        }

        jobs.Select(j => j.BatchCount).Should().Equal(2, 2, 3, 2, 2);
        h.Events(ids[0]).Should().Contain(e => e.Message == "Job #1 started: customer1.pdf.");
        h.Events(ids[1]).Should().Contain(e => e.Message == "Job #2 started: customer2.pdf.");
    }

    [Fact]
    public async Task ASinglePdfStillStartsStraightAway()
    {
        await using var h = await EngineHarness.CreateAsync(20);
        var job = await h.CreateJobAsync(pagesPerBatch: 10, delayMinutes: 30);

        h.Engine.StartJob(job.Id);

        (await h.WaitForStateAsync(JobState.CoolingDown)).Should().BeTrue(h.Diagnostics());
        h.PrintEngine.SubmissionCount.Should().Be(1);
        h.SkipDelay(31);
        (await h.WaitForStateAsync(JobState.Completed)).Should().BeTrue(h.Diagnostics());
        h.Printer.TotalPagesPrinted.Should().Be(20);
    }

    // ---- Invoices ---------------------------------------------------------------------------

    [Fact]
    public async Task EveryJobPrintsItsOwnInvoiceAtItsOwnPrices()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var one = h.AddDocument(4, "one.pdf", colourPages: new[] { 2 });
        var two = h.AddDocument(3, "two.pdf");
        var three = h.AddDocument(5, "three.pdf", colourPages: new[] { 1, 5 });

        JobConfiguration Billed(PdfDocumentInfo doc, decimal bw, decimal colour) => h.Config(10, 0) with
        {
            Invoice = new InvoiceRequest(bw, colour, PageColourMap.Encode(PageColourAnalyzer.Analyse(doc.Path))),
        };

        var a = await h.CreateJobAsync(one, Billed(one, 2m, 10m));
        var b = await h.CreateJobAsync(two, h.Config(10, 0));
        var c = await h.CreateJobAsync(three, Billed(three, 3m, 15m));

        h.Engine.StartQueue();
        (await h.RunQueueToEndAsync(new[] { a.Id, b.Id, c.Id })).Should().BeTrue(h.Diagnostics());
        (await h.WaitUntilAsync(() => h.InvoicePrinter.Printed.Count == 2 &&
                                      h.Snapshot(c.Id).InvoiceState == InvoiceState.Printed)).Should().BeTrue(h.Diagnostics());

        var invoices = h.InvoicePrinter.Printed;
        invoices.Should().HaveCount(2, "one per job that asked for one, never one for the whole queue");
        invoices[0].Total.Should().Be(3 * 2m + 1 * 10m);
        invoices[1].Total.Should().Be(3 * 3m + 2 * 15m);
        h.Snapshot(b.Id).InvoiceState.Should().Be(InvoiceState.NotRequested);
    }

    // ---- Older databases ---------------------------------------------------------------------

    [Fact]
    public void AnOlderDatabaseBecomesAQueueInTheOrderItsJobsWereCreated()
    {
        var root = Path.Combine(Path.GetTempPath(), "printpacer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "jobs.db");
        var t0 = new DateTimeOffset(2026, 9, 1, 20, 0, 0, TimeSpan.Zero);

        PrintJob Job(string name, DateTimeOffset created, JobState state)
        {
            var job = PrintJob.Create(Guid.NewGuid().ToString("N"), 0, 0,
                new DocumentIdentity($@"C:\docs\{name}", name, 100, "AB", 20), new JobConfiguration
                {
                    PrinterName = "HP", PagesPerBatch = 10, DelayMinutes = 30,
                }, created);
            job.State = state;
            return job;
        }

        var later = Job("later.pdf", t0.AddHours(2), JobState.Draft);
        var earlier = Job("earlier.pdf", t0, JobState.CoolingDown);
        earlier.Batches[0].State = BatchState.Completed;

        try
        {
            using (var store = new JobStore(path))
            {
                store.InsertJob(later);      // inserted first, but created second
                store.InsertJob(earlier);
            }

            // Back to exactly what 1.3 wrote: schema v3, every queue position 0, no job numbers.
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE jobs SET queue_position = 0;
                    ALTER TABLE jobs DROP COLUMN job_number;
                    ALTER TABLE schema_info DROP COLUMN next_job_number;
                    UPDATE schema_info SET version = 3;
                    """;
                command.ExecuteNonQuery();
            }

            using (var store = new JobStore(path))
            {
                var jobs = store.LoadAllJobs();
                jobs.Select(j => j.FileName).Should().Equal("earlier.pdf", "later.pdf");
                jobs.Select(j => j.Number).Should().Equal(1, 2);
                jobs.Select(j => j.QueuePosition).Should().Equal(1, 2);

                // The job that was half way through is exactly as it was.
                jobs[0].State.Should().Be(JobState.CoolingDown);
                jobs[0].Batches.Select(b => b.State).Should().Equal(BatchState.Completed, BatchState.Pending);

                store.AllocateJobNumber().Should().Be(3, "numbers continue after the existing jobs");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* teardown */ }
        }
    }

    [Fact]
    public async Task JobNumbersAreNeverReusedAfterJobsAreCleared()
    {
        await using var h = await EngineHarness.CreateAsync(10);
        var a = await h.CreateJobAsync(h.AddDocument(5, "a.pdf"), h.Config(5, 0));
        var b = await h.CreateJobAsync(h.AddDocument(5, "b.pdf"), h.Config(5, 0));
        (await h.Engine.RemoveJobAsync(b.Id)).Should().BeNull();
        (await h.Engine.RemoveJobAsync(a.Id)).Should().BeNull();

        await h.RestartApplicationAsync();
        var c = await h.CreateJobAsync(h.AddDocument(5, "c.pdf"), h.Config(5, 0));
        c.Number.Should().Be(3);
    }

    /// <summary>Consecutive duplicates collapsed: A A B B B A becomes A B A.</summary>
    private static List<string> Collapse(IReadOnlyList<string> items)
    {
        var result = new List<string>();
        foreach (var item in items)
            if (result.Count == 0 || result[^1] != item) result.Add(item);
        return result;
    }
}
