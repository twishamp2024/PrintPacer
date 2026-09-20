using FluentAssertions;
using PrintPacer.Core.Engine;
using PrintPacer.Core.Model;
using Xunit;

namespace PrintPacer.Tests;

/// <summary>
/// The verifier decides whether paper came out. Getting this wrong in one direction wastes
/// fifty sheets; getting it wrong in the other silently drops pages out of a book.
///
/// The single most important property, asserted repeatedly below: only a verdict of
/// <see cref="AttemptState.Failed"/> permits an automatic resubmission. Everything
/// uncertain must stop and ask.
/// </summary>
public class BatchVerifierTests
{
    private static VerificationInput Input(Action<Builder> configure)
    {
        var builder = new Builder();
        configure(builder);
        return builder.Build();
    }

    private sealed class Builder
    {
        public int Expected = 50;
        public bool Spooled = true;
        public bool FailedBeforeOutput;
        public bool InQueue = true;
        public bool Printing = true;
        public int PagesPrinted;
        public bool PrintedFlag;
        public bool ErrorFlag;
        public bool StillQueued;
        public long? Delta;
        public long? JamDelta;
        public long? MispickDelta;
        public string? Error;

        public VerificationInput Build() => new()
        {
            ExpectedPages = Expected,
            SpooledSuccessfully = Spooled,
            FailedBeforeAnyOutput = FailedBeforeOutput,
            ObservedInQueue = InQueue,
            ObservedPrinting = Printing,
            SpoolerPagesPrinted = PagesPrinted,
            SpoolerReportedPrintedFlag = PrintedFlag,
            SpoolerReportedError = ErrorFlag,
            JobStillInQueue = StillQueued,
            DeviceImpressionDelta = Delta,
            DeviceJamDelta = JamDelta,
            DeviceMispickDelta = MispickDelta,
            SubmissionError = Error,
        };
    }

    // ---- The physical counter is believed -----------------------------------

    [Fact]
    public void ExactPhysicalPageCountConfirmsTheBatch()
    {
        var verdict = BatchVerifier.Decide(Input(b => { b.Delta = 50; b.PagesPrinted = 50; }));

        verdict.State.Should().Be(AttemptState.Verified);
        verdict.ConfirmedPages.Should().Be(50);
        verdict.Evidence.Should().HaveFlag(CompletionEvidence.DeviceCounterMatched);
    }

    [Fact]
    public void PhysicalCounterNotMovingMeansNothingPrinted()
    {
        var verdict = BatchVerifier.Decide(Input(b => { b.Delta = 0; b.Printing = false; }));

        verdict.State.Should().Be(AttemptState.Failed);
        verdict.SafeToAutoRetry.Should().BeTrue("nothing was marked on paper, so resending cannot duplicate");
        verdict.ConfirmedPages.Should().Be(0);
    }

    [Fact]
    public void PartialPhysicalCountIsAmbiguousAndReportsHowFarItGot()
    {
        var verdict = BatchVerifier.Decide(Input(b => { b.Delta = 18; b.PagesPrinted = 18; }));

        verdict.State.Should().Be(AttemptState.Ambiguous);
        verdict.SafeToAutoRetry.Should().BeFalse();
        verdict.ConfirmedPages.Should().Be(18);
        verdict.Explanation.Should().Contain("18");
    }

    [Fact]
    public void JamAndMispickCountsAreSurfacedInThePartialExplanation()
    {
        var verdict = BatchVerifier.Decide(Input(b =>
        {
            b.Delta = 12;
            b.JamDelta = 1;
            b.MispickDelta = 3;
        }));

        verdict.Explanation.Should().Contain("paper jam");
        verdict.Explanation.Should().Contain("feed failure");
    }

    [Fact]
    public void MorePagesThanExpectedCannotBeAttributed()
    {
        var verdict = BatchVerifier.Decide(Input(b => { b.Delta = 63; b.PagesPrinted = 50; }));

        verdict.State.Should().Be(AttemptState.Ambiguous);
        verdict.Evidence.Should().HaveFlag(CompletionEvidence.DeviceCounterOvershoot);
    }

    // ---- Conflicts are never resolved by guessing ---------------------------

    [Fact]
    public void WindowsSayingPrintedWhileTheCounterSaysNothingIsAmbiguous()
    {
        var verdict = BatchVerifier.Decide(Input(b =>
        {
            b.Delta = 0;
            b.PrintedFlag = true;
            b.PagesPrinted = 50;
        }));

        verdict.State.Should().Be(AttemptState.Ambiguous);
        verdict.SafeToAutoRetry.Should().BeFalse("a disagreement between Windows and the hardware must not be guessed");
        verdict.Explanation.Should().Contain("cannot both be right");
    }

    // ---- Falling back to the spooler alone ----------------------------------

    [Fact]
    public void WithoutACounterAPrintedFlagAndFullPageCountIsAccepted()
    {
        var verdict = BatchVerifier.Decide(Input(b =>
        {
            b.Delta = null;
            b.PrintedFlag = true;
            b.PagesPrinted = 50;
        }));

        verdict.State.Should().Be(AttemptState.Verified);
        verdict.Evidence.Should().HaveFlag(CompletionEvidence.DeviceProbeUnavailable);
    }

    [Fact]
    public void WithoutACounterAShortPageCountIsAmbiguous()
    {
        var verdict = BatchVerifier.Decide(Input(b =>
        {
            b.Delta = null;
            b.PagesPrinted = 31;
        }));

        verdict.State.Should().Be(AttemptState.Ambiguous);
        verdict.ConfirmedPages.Should().Be(31);
    }

    [Fact]
    public void AJobStillInTheQueueIsNotJudgedAtAll()
    {
        var verdict = BatchVerifier.Decide(Input(b => { b.Delta = null; b.StillQueued = true; }));

        verdict.State.Should().Be(AttemptState.Ambiguous);
        verdict.Explanation.Should().Contain("still in the Windows queue");
    }

    // ---- Submission failures -------------------------------------------------

    [Fact]
    public void FailingBeforeAnyOutputIsSafelyRetryable()
    {
        var verdict = BatchVerifier.Decide(Input(b =>
        {
            b.FailedBeforeOutput = true;
            b.Spooled = false;
            b.InQueue = false;
            b.Printing = false;
            b.Error = "Printer unavailable";
        }));

        verdict.State.Should().Be(AttemptState.Failed);
        verdict.SafeToAutoRetry.Should().BeTrue();
    }

    [Fact]
    public void FailingAfterPartialOutputIsNeverSafelyRetryable()
    {
        // The engine reported failure, but pages had already been handed over. Without a
        // counter there is no way to know what came out.
        var verdict = BatchVerifier.Decide(Input(b =>
        {
            b.FailedBeforeOutput = false;
            b.Spooled = false;
            b.Delta = null;
            b.InQueue = true;
            b.Printing = true;
            b.PagesPrinted = 20;
            b.Error = "Rendering failed on page 21";
        }));

        verdict.State.Should().Be(AttemptState.Ambiguous);
        verdict.SafeToAutoRetry.Should().BeFalse();
    }

    [Fact]
    public void ALargeBatchNeverSeenInTheQueueIsAmbiguousNotRetryable()
    {
        // 50 pages cannot pass through a two-second poll unnoticed, so "never seen" plus
        // "Windows accepted it" is genuinely unexplained and must not be resent blindly.
        var verdict = BatchVerifier.Decide(Input(b =>
        {
            b.Delta = null;
            b.Spooled = true;
            b.InQueue = false;
            b.Printing = false;
            b.Expected = 50;
        }));

        verdict.State.Should().Be(AttemptState.Ambiguous);
        verdict.SafeToAutoRetry.Should().BeFalse();
    }

    [Fact]
    public void ATinyBatchNeverSeenInTheQueueIsAlsoAmbiguous()
    {
        var verdict = BatchVerifier.Decide(Input(b =>
        {
            b.Delta = null;
            b.Spooled = true;
            b.InQueue = false;
            b.Printing = false;
            b.Expected = 2;
        }));

        verdict.State.Should().Be(AttemptState.Ambiguous);
        verdict.Explanation.Should().Contain("between status checks");
    }

    [Fact]
    public void ASpoolerErrorIsAmbiguousRatherThanAFailure()
    {
        var verdict = BatchVerifier.Decide(Input(b =>
        {
            b.Delta = null;
            b.ErrorFlag = true;
            b.PagesPrinted = 12;
        }));

        verdict.State.Should().Be(AttemptState.Ambiguous);
        verdict.SafeToAutoRetry.Should().BeFalse();
    }

    // ---- The global safety property ------------------------------------------

    [Fact]
    public void NoVerdictWithAnyPhysicalOutputIsEverSafelyRetryable()
    {
        // Exhaustively sweep the decision space and assert the invariant that protects the
        // user from a duplicate stack of paper.
        foreach (var delta in new long?[] { null, 0, 1, 25, 49, 50, 51, 100 })
        foreach (var spooled in new[] { true, false })
        foreach (var beforeOutput in new[] { true, false })
        foreach (var inQueue in new[] { true, false })
        foreach (var printing in new[] { true, false })
        foreach (var pages in new[] { 0, 25, 50 })
        foreach (var printedFlag in new[] { true, false })
        {
            var verdict = BatchVerifier.Decide(new VerificationInput
            {
                ExpectedPages = 50,
                SpooledSuccessfully = spooled,
                FailedBeforeAnyOutput = beforeOutput,
                ObservedInQueue = inQueue,
                ObservedPrinting = printing,
                SpoolerPagesPrinted = pages,
                SpoolerReportedPrintedFlag = printedFlag,
                DeviceImpressionDelta = delta,
            });

            if (!verdict.SafeToAutoRetry) continue;

            // If we are about to resend, the evidence must positively say nothing printed.
            var provesNothingPrinted =
                delta == 0 ||
                (beforeOutput && !inQueue) ||
                (!spooled && !inQueue && delta is null);

            provesNothingPrinted.Should().BeTrue(
                $"an automatic resend was allowed without proof that nothing printed " +
                $"(delta={delta?.ToString() ?? "null"}, spooled={spooled}, beforeOutput={beforeOutput}, " +
                $"inQueue={inQueue}, printing={printing}, pages={pages}, printedFlag={printedFlag})");
        }
    }
}
