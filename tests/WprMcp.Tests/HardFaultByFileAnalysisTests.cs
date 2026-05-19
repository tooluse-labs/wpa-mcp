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
    }

    [Fact]
    public void HardFaultByFile_AppliesHalfOpenWindow()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var fullTrace = tools.HardFaultByFile(FixturePath, top: 100);
        Assert.NotEmpty(fullTrace.Rows);

        var emptyWindow = tools.HardFaultByFile(FixturePath, top: 100, startUs: long.MaxValue - 1, endUs: long.MaxValue);

        Assert.Empty(emptyWindow.Rows);
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
