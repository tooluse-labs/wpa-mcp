using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class HardFaultByFileAnalysisTests
{
    private const string FixturePath = "fixtures/small_mmap.etl";

    [Fact]
    public void HardFaultByFile_ReturnsAtLeastOneRow()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var resp = tools.HardFaultByFile(FixturePath, top: 10);
        Assert.NotEmpty(resp.Rows);
    }

    [Fact]
    public void HardFaultByFile_AlwaysIncludesKeywordHint()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var resp = tools.HardFaultByFile(FixturePath, top: 10);
        Assert.Contains(resp.Warnings, w => w.Contains("MemoryHardFaults"));
    }

    [Fact]
    public void HardFaultByFile_CanSortByMaxLatency()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var resp = tools.HardFaultByFile(FixturePath, top: 100, orderBy: "max_latency");

        Assert.NotEmpty(resp.Rows);
        Assert.True(resp.Rows.Zip(resp.Rows.Skip(1), (a, b) => a.MaxLatencyUs >= b.MaxLatencyUs).All(v => v));
        Assert.All(resp.Rows, row => Assert.True(row.MaxLatencyTimeUs >= 0));
    }

    [Fact]
    public void HardFaultByFile_ReportsMaxLatencyTimestamp()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var fullTrace = tools.HardFaultByFile(FixturePath, top: 100, orderBy: "max_latency");
        var slowest = fullTrace.Rows[0];

        var singleTimestampWindow = tools.HardFaultByFile(
            FixturePath,
            top: 100,
            startUs: slowest.MaxLatencyTimeUs,
            endUs: slowest.MaxLatencyTimeUs + 1,
            orderBy: "max_latency");

        var row = Assert.Single(singleTimestampWindow.Rows.Where(row => row.File == slowest.File));
        Assert.Equal(slowest.MaxLatencyUs, row.MaxLatencyUs);
        Assert.Equal(slowest.MaxLatencyTimeUs, row.MaxLatencyTimeUs);
    }

    [Fact]
    public void HardFaultByFile_RejectsWindowBeyondTrace()
    {
        var cache = new TraceCache(capacity: 2);
        var tools = new HardFaultTools(cache);
        var fullTrace = tools.HardFaultByFile(FixturePath, top: 100);
        Assert.NotEmpty(fullTrace.Rows);
        var trace = cache.Get(FixturePath);
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);

        Assert.Throws<ArgumentOutOfRangeException>(() => tools.HardFaultByFile(
            FixturePath, top: 100, startUs: traceEndUs, endUs: traceEndUs + 1));
    }

    [Fact]
    public void HardFaultByFile_RejectsBadTop()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.HardFaultByFile("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.HardFaultByFile("nonexistent.etl", top: 1001));
    }

    [Fact]
    public void HardFaultByFile_RejectsBadOrderBy()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentException>(() => tools.HardFaultByFile("nonexistent.etl", orderBy: "duration"));
    }
}
