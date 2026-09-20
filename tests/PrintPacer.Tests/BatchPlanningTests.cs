using FluentAssertions;
using PrintPacer.Core.Model;
using Xunit;

namespace PrintPacer.Tests;

/// <summary>
/// The batch plan decides which page goes on which sheet. An off-by-one here is not a
/// cosmetic bug: it silently prints the wrong pages, or skips one entirely.
/// </summary>
public class BatchPlanningTests
{
    [Theory]
    [InlineData(1, 50, 1)]      // batch size larger than the document
    [InlineData(10, 50, 1)]
    [InlineData(50, 50, 1)]     // exactly one full batch
    [InlineData(51, 50, 2)]     // one page over: second batch holds a single page
    [InlineData(200, 50, 4)]    // the example from the brief
    [InlineData(203, 50, 5)]    // not divisible by the batch size
    [InlineData(200, 1, 200)]   // batch size of one
    [InlineData(1, 1, 1)]
    public void ProducesTheExpectedNumberOfBatches(int pages, int perBatch, int expected)
    {
        PrintJob.PlanBatches("job", pages, perBatch).Should().HaveCount(expected);
    }

    [Fact]
    public void CoversEveryPageExactlyOnceInOrder()
    {
        foreach (var pages in new[] { 1, 2, 7, 10, 49, 50, 51, 99, 100, 199, 200, 203, 500 })
        foreach (var perBatch in new[] { 1, 2, 7, 25, 50, 51, 200, 1000 })
        {
            var batches = PrintJob.PlanBatches("job", pages, perBatch);

            var covered = batches.SelectMany(b => Enumerable.Range(b.FirstPage, b.PageCount)).ToList();

            covered.Should().BeInAscendingOrder(
                $"pages must print in order for {pages} pages at {perBatch}/batch");
            covered.Should().OnlyHaveUniqueItems(
                $"no page may be printed twice for {pages} pages at {perBatch}/batch");
            covered.Should().Equal(Enumerable.Range(1, pages),
                $"every page must be printed exactly once for {pages} pages at {perBatch}/batch");

            batches.Select(b => b.Number).Should().Equal(Enumerable.Range(1, batches.Count));
        }
    }

    [Fact]
    public void MatchesTheWorkedExampleFromTheBrief()
    {
        var batches = PrintJob.PlanBatches("job", 200, 50);

        batches.Select(b => (b.Number, b.FirstPage, b.LastPage)).Should().Equal(
            (1, 1, 50),
            (2, 51, 100),
            (3, 101, 150),
            (4, 151, 200));
    }

    [Fact]
    public void FinalBatchAbsorbsTheRemainder()
    {
        var batches = PrintJob.PlanBatches("job", 203, 50);

        batches.Should().HaveCount(5);
        batches[^1].FirstPage.Should().Be(201);
        batches[^1].LastPage.Should().Be(203);
        batches[^1].PageCount.Should().Be(3);
    }

    [Fact]
    public void RejectsNonsensicalInput()
    {
        var planZeroPages = () => PrintJob.PlanBatches("job", 0, 50);
        var planNegative = () => PrintJob.PlanBatches("job", -5, 50);
        var planZeroBatch = () => PrintJob.PlanBatches("job", 10, 0);

        planZeroPages.Should().Throw<ArgumentOutOfRangeException>();
        planNegative.Should().Throw<ArgumentOutOfRangeException>();
        planZeroBatch.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SinglePageBatchesAreLabelledWithoutARange()
    {
        var batches = PrintJob.PlanBatches("job", 51, 50);

        batches[0].RangeLabel.Should().Be("Pages 1–50");
        batches[1].RangeLabel.Should().Be("Page 51");
    }
}
