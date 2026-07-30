using WprMcp.Core;

namespace WprMcp.Tests;

public sealed class TimeWindowTests
{
    [Theory]
    [InlineData(0.0000, 0)]
    [InlineData(1.2349, 1234)]
    [InlineData(1.9999, 1999)]
    public void FromMilliseconds_FloorsOnce(double milliseconds, long expectedUs) =>
        Assert.Equal(expectedUs, TraceTime.FromMilliseconds(milliseconds));

    [Theory]
    [InlineData(-5, 0, 0)]
    [InlineData(0, 10, 10)]
    [InlineData(5, 15, 10)]
    [InlineData(15, 25, 5)]
    [InlineData(0, 30, 20)]
    [InlineData(20, 30, 0)]
    public void IntersectDurationUs_UsesHalfOpenOverlap(long intervalStart, long intervalEnd, long expected) =>
        Assert.Equal(expected, new TimeWindow(0, 20).IntersectDurationUs(intervalStart, intervalEnd));

    [Theory]
    [InlineData(-5, 0, 0)]
    [InlineData(5, 0, 5)]
    [InlineData(15, 20, 20)]
    public void ClipStart_UsesCanonicalLowerBoundary(
        long intervalStartUs,
        long windowStartUs,
        long expectedUs) =>
        Assert.Equal(expectedUs, TimeWindow.ClipStart(intervalStartUs, windowStartUs));

    [Theory]
    [InlineData(25, 20, 20)]
    [InlineData(15, 20, 15)]
    [InlineData(0, 0, 0)]
    public void ClipEnd_UsesCanonicalUpperBoundary(
        long intervalEndUs,
        long windowEndUs,
        long expectedUs) =>
        Assert.Equal(expectedUs, TimeWindow.ClipEnd(intervalEndUs, windowEndUs));

    [Fact]
    public void Resolve_RejectsEmptyOneSidedWindowAfterTraceDurationIsKnown()
    {
        var input = TimeWindowInput.Validate(startUs: 100, endUs: null, maxDurationUs: 1_000);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            input.Resolve(traceDurationUs: 100, maxDurationUs: 1_000));
    }
}
