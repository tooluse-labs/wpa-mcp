using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class PageFaultStackAnalysisTests
{
    private const string MmapFixture = "fixtures/small_mmap.etl"; // captured with HardFaults keyword

    [Fact]
    public void HardFaultTopStacks_OnFixtureWithHardFaultsReturnsRows()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var resp = tools.HardFaultTopStacks(MmapFixture, top: 30);
        Assert.NotEmpty(resp.Rows);
        Assert.True(resp.TotalPageInBytes > 0);
        Assert.True(resp.TotalFaultCount > 0);
    }

    [Fact]
    public void HardFaultTopStacks_RowsOrderedByExclusiveBytesDesc()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var resp = tools.HardFaultTopStacks(MmapFixture, top: 50);
        for (var i = 1; i < resp.Rows.Count; i++)
            Assert.True(resp.Rows[i - 1].ExclusivePageInBytes >= resp.Rows[i].ExclusivePageInBytes);
    }

    [Fact]
    public void HardFaultTopStacks_AlwaysIncludesKeywordHint()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var resp = tools.HardFaultTopStacks(MmapFixture, top: 5);
        Assert.Contains(resp.Warnings, w => w.Contains("MemoryHardFaults"));
    }

    [Fact]
    public void HardFaultTopStacks_RejectsBadInput()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.HardFaultTopStacks("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.HardFaultTopStacks("nonexistent.etl", top: 1001));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.HardFaultTopStacks("nonexistent.etl", whenBuckets: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.HardFaultTopStacks("nonexistent.etl", whenBuckets: 1001));
    }

    [Fact]
    public void HardFaultTopStacks_FaultCountTracksAlongsidesBytes()
    {
        // Each row carries both ExclusivePageInBytes (metric=ByteCount, ranks the table) and
        // ExclusiveFaultCount (one per event). For any row with bytes > 0, fault count should
        // also be > 0 — the count source mirrors the bytes source one-to-one.
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var resp = tools.HardFaultTopStacks(MmapFixture, top: 20);
        Assert.All(resp.Rows.Where(r => r.ExclusivePageInBytes > 0),
            r => Assert.True(r.ExclusiveFaultCount > 0,
                $"row {r.Function} has bytes>0 but faultCount=0"));
    }

    [Fact]
    public void HardFaultTopStacks_WhenBucketsPopulatesHistogram()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var resp = tools.HardFaultTopStacks(MmapFixture, top: 10, whenBuckets: 20);
        Assert.NotNull(resp.When);
        Assert.Equal(20, resp.When!.Buckets.Length);
        Assert.True(resp.When.Buckets.Sum() <= resp.TotalPageInBytes);
    }

    [Fact]
    public void HardFaultCallerCallee_ReturnsExpectedShapeForFrameInTrace()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var topResp = tools.HardFaultTopStacks(MmapFixture, top: 5);
        if (topResp.Rows.Count == 0) return;
        var picked = topResp.Rows[0].Function;

        var ccResp = tools.HardFaultCallerCallee(MmapFixture, function: picked, top: 10);
        Assert.Equal(picked, ccResp.FocusFunction);
        Assert.Equal("pageInBytes", ccResp.MetricName);
        Assert.True(ccResp.FocusInclusiveMetric > 0);
    }

    [Fact]
    public void HardFaultCallerCallee_RejectsBadInput()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.HardFaultCallerCallee("nonexistent.etl", function: "x", top: 0));
        Assert.Throws<ArgumentException>(() =>
            tools.HardFaultCallerCallee("nonexistent.etl", function: "", top: 10));
    }
}
