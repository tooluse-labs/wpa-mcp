using WpaMcp.Analyzers;
using WpaMcp.Core;
using Xunit;

namespace WpaMcp.Tests;

public sealed class DurationAccountingTests
{
    [Theory]
    [InlineData(90, 130, 40, 30)]
    [InlineData(170, 210, 40, 30)]
    [InlineData(90, 210, 120, 100)]
    public void PairBeforeClip_OverlapRetainsFullDuration(
        long intervalStartUs,
        long intervalEndUs,
        long expectedFullDurationUs,
        long expectedAccountedDurationUs)
    {
        var pairs = new IntervalPairAccumulator<string, string, string>();
        pairs.AddStart("scan-1", intervalStartUs, "start");
        pairs.AddStop("scan-1", intervalEndUs, "stop");

        var pair = Assert.Single(pairs.Complete().Pairs);
        var projected = Assert.IsType<AccountedPairedInterval<string, string, string>>(
            DurationAccounting.Project(pair, new TimeWindow(100, 200)));

        Assert.Equal(expectedFullDurationUs, projected.FullDurationUs);
        Assert.Equal(expectedAccountedDurationUs, projected.AccountedDurationUs);
        Assert.Equal("clipped_overlap_v2", projected.AccountingMode);
    }

    [Theory]
    [InlineData(20, 30, 0, 20)]
    [InlineData(20, 30, 30, 40)]
    [InlineData(20, 30, 0, 10)]
    [InlineData(20, 30, 40, 50)]
    public void Project_TouchingOrDisjointIntervalReturnsNull(
        long intervalStartUs,
        long intervalEndUs,
        long windowStartUs,
        long windowEndUs)
    {
        var pair = new PairedInterval<int, string, string>(
            1, intervalStartUs, intervalEndUs, "start", "stop");

        Assert.Null(DurationAccounting.Project(
            pair, new TimeWindow(windowStartUs, windowEndUs)));
    }

    [Fact]
    public void Complete_PairsRepeatedKeyFifoAndReportsInvalidAndUnmatched()
    {
        var pairs = new IntervalPairAccumulator<int, string, string>();
        pairs.AddStop(7, 5, "orphan-stop");
        pairs.AddStart(7, 10, "first");
        pairs.AddStart(7, 20, "second");
        pairs.AddStop(7, 15, "first-stop");
        pairs.AddStop(7, 19, "backward-stop");
        pairs.AddStart(8, 30, "orphan-start");

        var result = pairs.Complete();

        var pair = Assert.Single(result.Pairs);
        Assert.Equal(10, pair.StartUs);
        Assert.Equal(15, pair.EndUs);
        Assert.Equal("first", pair.StartData);
        Assert.Equal("first-stop", pair.StopData);
        Assert.Single(result.InvalidIntervals);
        Assert.Single(result.UnmatchedStarts);
        Assert.Single(result.UnmatchedStops);
    }

    [Fact]
    public void Complete_IsIdempotentAndRejectsFurtherWrites()
    {
        var pairs = new IntervalPairAccumulator<int, string, string>();
        pairs.AddStart(1, 10, "start");

        var first = pairs.Complete();
        var second = pairs.Complete();

        Assert.Same(first, second);
        Assert.Single(first.UnmatchedStarts);
        Assert.Throws<InvalidOperationException>(
            new Action(() => pairs.AddStart(1, 20, "late-start")));
        Assert.Throws<InvalidOperationException>(
            new Action(() => pairs.AddStop(1, 20, "late-stop")));
    }
}
