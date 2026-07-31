using WpaMcp.Analyzers;
using WpaMcp.Core;
using Xunit;

namespace WpaMcp.Tests;

public class StackSourceTopNTests
{
    [Fact]
    public void AddInterval_SplitsMetricAcrossBuckets()
    {
        var histogram = StackSourceTopN.WhenHistogram.ForWindow(
            new TimeWindow(0, 100), bucketCount: 2);

        histogram.AddDurationInterval(intervalStartUs: 25, intervalEndUs: 75);

        var result = Assert.IsType<WpaMcp.Output.TimeHistogram>(histogram.Build());
        Assert.Equal(100, result.EndUs);
        Assert.Equal(new long[] { 25, 25 }, result.Buckets);
    }

    [Fact]
    public void AddInterval_UsesHalfOpenBoundsAndPreservesUntouchedBuckets()
    {
        var histogram = StackSourceTopN.WhenHistogram.ForWindow(
            new TimeWindow(10, 95), bucketCount: 10);

        histogram.AddDurationInterval(intervalStartUs: 28, intervalEndUs: 29);

        var result = Assert.IsType<WpaMcp.Output.TimeHistogram>(histogram.Build());
        Assert.Equal(9, result.BucketWidthUs);
        Assert.Equal(
            new long[] { 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 },
            result.Buckets);
    }

    [Fact]
    public void Pct_ClampsFloatingPointOvershoot()
    {
        Assert.Equal(100.0, StackSourceTopN.Pct(total: 100, n: 100.01));
        Assert.Equal(0.0, StackSourceTopN.Pct(total: 100, n: -1));
        Assert.Equal(0.0, StackSourceTopN.Pct(total: 0, n: 1));
    }

    [Fact]
    public void PctOfTrace_OnlyEmitsForFilteredViews()
    {
        Assert.Null(StackSourceTopN.PctOfTrace(hasFilter: false, traceTotal: 100, n: 50));
        Assert.Equal(50.0, StackSourceTopN.PctOfTrace(hasFilter: true, traceTotal: 100, n: 50));
        Assert.Equal(100.0, StackSourceTopN.PctOfTrace(hasFilter: true, traceTotal: 100, n: 100.01));
    }
}
