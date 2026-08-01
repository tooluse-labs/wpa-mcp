using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class ImageLoadStackAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";
    private const string MmapFixture = "fixtures/small_mmap.etl"; // contains 8 spawned processes,
                                                                  // so it has plenty of ImageLoad events

    [Fact]
    public void ImageLoadTopStacks_ReturnsRowsOrEmitsKeywordWarning()
    {
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        var resp = tools.ImageLoadTopStacks(FixturePath, top: 20);
        if (resp.Rows.Count == 0)
            Assert.Contains(resp.Warnings,
                w => w.Contains("ImageLoad", StringComparison.OrdinalIgnoreCase));
        else
            Assert.True(resp.TotalLoads > 0);
    }

    [Fact]
    public void ImageLoadTopStacks_RowsOrderedByExclusiveLoadsDesc()
    {
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        var resp = tools.ImageLoadTopStacks(FixturePath, top: 50);
        for (var i = 1; i < resp.Rows.Count; i++)
            Assert.True(resp.Rows[i - 1].ExclusiveLoads >= resp.Rows[i].ExclusiveLoads);
    }

    [Fact]
    public void ImageLoadTopStacks_RejectsBadInput()
    {
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ImageLoadTopStacks("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ImageLoadTopStacks("nonexistent.etl", top: 1001));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ImageLoadTopStacks("nonexistent.etl", whenBuckets: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ImageLoadTopStacks("nonexistent.etl", whenBuckets: 1001));
    }

    [Fact]
    public void ImageLoadTopStacks_OnMmapFixtureReturnsLoadsAndCoverage()
    {
        if (!File.Exists(MmapFixture)) return;
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        var resp = tools.ImageLoadTopStacks(MmapFixture, top: 30);
        // small_mmap.etl spawns 8 processes during capture → there must be ImageLoad events.
        Assert.True(resp.TotalLoads > 0,
            $"expected ImageLoad events on small_mmap fixture, got {resp.TotalLoads} (warnings: " +
            $"{string.Join(", ", resp.Warnings)})");
        Assert.NotEmpty(resp.Rows);
        var coverage = Assert.IsType<WpaMcp.Output.DomainStackCoverage>(resp.StackCoverage);
        Assert.Equal("image_load", coverage.Domain);
        Assert.Equal(resp.TotalLoads, coverage.TotalEventCount);
        Assert.Equal(resp.TotalLoads, coverage.TotalMetric);
    }

    [Fact]
    public void ImageLoadTopStacks_WhenBucketsPopulatesHistogram()
    {
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        var resp = tools.ImageLoadTopStacks(FixturePath, top: 10, whenBuckets: 20);
        if (resp.TotalLoads == 0) return; // no events to bucket
        Assert.NotNull(resp.When);
        Assert.Equal(20, resp.When!.Buckets.Length);
        Assert.True(resp.When.BucketWidthUs > 0);
        Assert.True(resp.When.Buckets.Sum() <= resp.TotalLoads,
            "histogram total cannot exceed event total");
    }

    [Fact]
    public void ImageLoadTopStacks_ZeroWhenBucketsLeavesWhenNull()
    {
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        var resp = tools.ImageLoadTopStacks(FixturePath, top: 10, whenBuckets: 0);
        Assert.Null(resp.When);
    }

    [Fact]
    public void ImageLoadCallerCallee_ReturnsExpectedShapeForFrameInTrace()
    {
        if (!File.Exists(MmapFixture)) return;
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        var topResp = tools.ImageLoadTopStacks(MmapFixture, top: 5);
        if (topResp.Rows.Count == 0) return;
        var picked = topResp.Rows[0].Function;

        var ccResp = tools.ImageLoadCallerCallee(MmapFixture, function: picked, top: 10);
        Assert.Equal(picked, ccResp.FocusFunction);
        Assert.Equal("loads", ccResp.MetricName);
        Assert.True(ccResp.FocusInclusiveMetric > 0);
        Assert.Equal(topResp.StackCoverage, ccResp.StackCoverage);
    }

    [Fact]
    public void ImageLoadCallerCallee_RejectsBadInput()
    {
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.ImageLoadCallerCallee("nonexistent.etl", function: "x", top: 0));
        Assert.Throws<ArgumentException>(() =>
            tools.ImageLoadCallerCallee("nonexistent.etl", function: "", top: 10));
    }
}
