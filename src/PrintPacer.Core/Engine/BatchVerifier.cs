using PrintPacer.Core.Model;

namespace PrintPacer.Core.Engine;

/// <summary>Everything known about one submission at the moment we must judge it.</summary>
public sealed record VerificationInput
{
    public required int ExpectedPages { get; init; }

    /// <summary>The print engine reported that all pages reached the spooler.</summary>
    public bool SpooledSuccessfully { get; init; }

    /// <summary>The submission failed before a single page was handed over.</summary>
    public bool FailedBeforeAnyOutput { get; init; }

    public bool ObservedInQueue { get; init; }
    public bool ObservedPrinting { get; init; }
    public int SpoolerPagesPrinted { get; init; }
    public bool SpoolerReportedPrintedFlag { get; init; }
    public bool SpoolerReportedError { get; init; }

    /// <summary>The job is still sitting in the queue right now.</summary>
    public bool JobStillInQueue { get; init; }

    /// <summary>
    /// Change in the printer's physical impression counter across the submission.
    /// Null means the device could not be consulted, which is a different thing from zero.
    /// </summary>
    public long? DeviceImpressionDelta { get; init; }

    public long? DeviceJamDelta { get; init; }
    public long? DeviceMispickDelta { get; init; }

    public string? SubmissionError { get; init; }
}

public sealed record VerificationVerdict(
    AttemptState State,
    CompletionEvidence Evidence,
    int ConfirmedPages,
    string Explanation)
{
    /// <summary>
    /// Only a definitively failed attempt may be resubmitted without a human deciding.
    /// Ambiguity is never resolved by guessing, because the cost of guessing wrong is
    /// a duplicate stack of paper.
    /// </summary>
    public bool SafeToAutoRetry => State == AttemptState.Failed;
}

/// <summary>
/// Decides whether a batch actually printed.
///
/// Two independent evidence channels are combined:
///
///   Spooler  - what Windows did with the job. Authoritative about handover, but on a
///              consumer inkjet it reports "printed" as soon as the data has been pushed
///              to the device, which is not the same as paper in the tray.
///
///   Device   - the printer's own lifetime impression counter. This is physical: it only
///              advances when a sheet is actually marked. It is the closest thing to
///              ground truth available without a human looking at the output tray.
///
/// The rules below are deliberately conservative. Where the two channels disagree, or
/// where neither can answer, the verdict is Ambiguous and a human is asked. An application
/// that confidently guesses here is an application that eventually reprints fifty pages.
/// </summary>
public static class BatchVerifier
{
    /// <summary>
    /// A short batch can slip entirely between two spooler polls, so "we never saw it in
    /// the queue" is only meaningful evidence for batches large enough that we could not
    /// possibly have missed them.
    /// </summary>
    public const int PagesTooFewToMissInQueue = 8;

    public static VerificationVerdict Decide(VerificationInput input)
    {
        var expected = input.ExpectedPages;

        // ---- 0. Nothing was ever handed over -------------------------------
        // The engine failed before writing a single page into the spool file. There is no
        // job, so there is nothing that could print. This is the one case we can retry
        // freely, and it is the common case for "printer was switched off".
        if (input.FailedBeforeAnyOutput && !input.ObservedInQueue)
        {
            return new VerificationVerdict(
                AttemptState.Failed,
                CompletionEvidence.VanishedUnobserved,
                0,
                $"The batch was never handed to Windows ({input.SubmissionError ?? "submission failed"}). " +
                "Nothing can have printed, so it is safe to try again.");
        }

        // ---- 1. Physical counter: the strongest evidence we have -----------
        if (input.DeviceImpressionDelta is { } delta)
        {
            if (delta == expected)
            {
                var evidence = CompletionEvidence.DeviceCounterMatched;
                if (input.SpoolerReportedPrintedFlag) evidence |= CompletionEvidence.SpoolerReportedPrinted;
                if (input.ObservedPrinting) evidence |= CompletionEvidence.SpoolerVanishedAfterPrinting;

                return new VerificationVerdict(AttemptState.Verified, evidence, expected,
                    $"The printer's own page counter advanced by exactly {expected}, " +
                    "which confirms the sheets physically printed.");
            }

            if (delta == 0)
            {
                // The counter is physical. If it did not move, no paper was marked.
                if (input.SpoolerReportedPrintedFlag || input.SpoolerPagesPrinted >= expected)
                {
                    // Windows and the hardware disagree. Never resolve this by guessing.
                    return new VerificationVerdict(
                        AttemptState.Ambiguous,
                        CompletionEvidence.DeviceCounterZero | CompletionEvidence.SpoolerReportedPrinted,
                        0,
                        $"Windows reported the batch as printed, but the printer's page counter did not move. " +
                        "These cannot both be right. Please check the output tray before continuing.");
                }

                return new VerificationVerdict(
                    AttemptState.Failed,
                    CompletionEvidence.DeviceCounterZero,
                    0,
                    "The printer's page counter did not move, so no sheets were printed. " +
                    "It is safe to send this batch again." + JamNote(input));
            }

            if (delta > 0 && delta < expected)
            {
                return new VerificationVerdict(
                    AttemptState.Ambiguous,
                    CompletionEvidence.DeviceCounterPartial,
                    (int)delta,
                    $"The printer produced {delta} of {expected} sheets, so the batch stopped part-way. " +
                    $"Check the output tray: the pages after the first {delta} of this batch are the ones " +
                    "still needed." + JamNote(input));
            }

            // More sheets than we asked for: something else printed at the same time, so the
            // counter can no longer be attributed to us.
            return new VerificationVerdict(
                AttemptState.Ambiguous,
                CompletionEvidence.DeviceCounterOvershoot,
                0,
                $"The printer's counter advanced by {delta} while this batch of {expected} pages was running, " +
                "so something else printed at the same time and the count cannot be attributed. " +
                "Please check the output tray.");
        }

        // ---- 2. No physical counter: fall back to the spooler --------------
        var noDevice = CompletionEvidence.DeviceProbeUnavailable;

        if (input.JobStillInQueue)
        {
            return new VerificationVerdict(
                AttemptState.Ambiguous,
                noDevice | CompletionEvidence.ReattachedAfterRestart,
                Math.Min(input.SpoolerPagesPrinted, expected),
                "The print job is still in the Windows queue. Its outcome is not yet decided.");
        }

        if (input.SpoolerReportedPrintedFlag && input.SpoolerPagesPrinted >= expected)
        {
            return new VerificationVerdict(
                AttemptState.Verified,
                noDevice | CompletionEvidence.SpoolerReportedPrinted | CompletionEvidence.SpoolerPagesPrintedMatched,
                expected,
                $"Windows reported the job as printed with all {expected} pages delivered. " +
                "No physical page counter was available to corroborate this.");
        }

        if (input.ObservedPrinting && input.SpoolerPagesPrinted >= expected)
        {
            return new VerificationVerdict(
                AttemptState.Verified,
                noDevice | CompletionEvidence.SpoolerVanishedAfterPrinting | CompletionEvidence.SpoolerPagesPrintedMatched,
                expected,
                $"The job was seen printing, reached {input.SpoolerPagesPrinted} of {expected} pages, " +
                "and then left the queue normally.");
        }

        if (input.ObservedPrinting && input.SpoolerPagesPrinted > 0)
        {
            return new VerificationVerdict(
                AttemptState.Ambiguous,
                noDevice | CompletionEvidence.SpoolerVanishedAfterPrinting,
                input.SpoolerPagesPrinted,
                $"The job left the Windows queue after {input.SpoolerPagesPrinted} of {expected} pages. " +
                "It may have finished without Windows updating the count, or it may have been cut short. " +
                "Please check the output tray.");
        }

        if (input.SpoolerReportedError)
        {
            return new VerificationVerdict(
                AttemptState.Ambiguous,
                noDevice | CompletionEvidence.SpoolerReportedError,
                input.SpoolerPagesPrinted,
                $"The print job reported an error after {input.SpoolerPagesPrinted} of {expected} pages. " +
                "Please check the output tray before deciding whether to resend it.");
        }

        if (!input.ObservedInQueue)
        {
            // We never saw it. For a batch of any real size that means it never got in,
            // because a 50-page job cannot pass through a two-second poll unseen. For a
            // very small batch it genuinely might have.
            if (!input.SpooledSuccessfully)
            {
                return new VerificationVerdict(
                    AttemptState.Failed,
                    noDevice | CompletionEvidence.VanishedUnobserved,
                    0,
                    $"The batch never reached the Windows queue ({input.SubmissionError ?? "no job was created"}). " +
                    "It is safe to try again.");
            }

            if (expected >= PagesTooFewToMissInQueue)
            {
                return new VerificationVerdict(
                    AttemptState.Ambiguous,
                    noDevice | CompletionEvidence.VanishedUnobserved,
                    0,
                    $"Windows accepted all {expected} pages but the job was never seen in the queue and is " +
                    "not there now. It may have printed, or it may have been deleted. " +
                    "Please check the output tray.");
            }

            return new VerificationVerdict(
                AttemptState.Ambiguous,
                noDevice | CompletionEvidence.VanishedUnobserved,
                0,
                $"This batch of {expected} page(s) was short enough to finish between status checks, " +
                "so it cannot be confirmed either way. Please check the output tray.");
        }

        // Seen in the queue, gone now, never observed actually printing, no page counts.
        return new VerificationVerdict(
            AttemptState.Ambiguous,
            noDevice | CompletionEvidence.VanishedUnobserved,
            input.SpoolerPagesPrinted,
            $"The job entered the Windows queue and has now gone, but was never observed printing and " +
            $"reported {input.SpoolerPagesPrinted} of {expected} pages. Please check the output tray.");
    }

    private static string JamNote(VerificationInput input)
    {
        var notes = new List<string>();
        if (input.DeviceJamDelta is > 0) notes.Add($"{input.DeviceJamDelta} paper jam(s)");
        if (input.DeviceMispickDelta is > 0) notes.Add($"{input.DeviceMispickDelta} paper feed failure(s)");
        return notes.Count == 0
            ? ""
            : $" The printer recorded {string.Join(" and ", notes)} during this batch.";
    }
}
