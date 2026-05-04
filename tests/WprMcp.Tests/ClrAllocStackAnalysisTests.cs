using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class ClrAllocStackAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ClrAllocTopStacks_NoMatchingEvents_ReturnsZeroMetricsAndWarns()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrAllocTopStacks(FixturePath);
        Assert.Equal(0, resp.TotalBytes);
        Assert.Equal(0, resp.TotalEventCount);
        StackAssertions.AssertRootOnly(resp.Rows, r => r.ExclusiveBytes, r => r.InclusiveBytes);
        Assert.Empty(resp.TopTypes);
        Assert.Contains(resp.Warnings, w => w.Contains("CLR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClrAllocCallerCallee_NoMatchingEvents_NoFocusMatch()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrAllocCallerCallee(FixturePath, "System.String..ctor");
        Assert.Equal(0, resp.FocusInclusiveMetric);
        Assert.Empty(resp.Callers);
        Assert.Empty(resp.Callees);
    }

    [Fact]
    public void ClrAllocTopStacks_RejectsBadInput()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ClrAllocTopStacks("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ClrAllocTopStacks("nonexistent.etl", top: 1001));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ClrAllocTopStacks("nonexistent.etl", whenBuckets: -1));
    }

    [Fact]
    public void ClrAllocCallerCallee_RejectsBadInput()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentException>(() => tools.ClrAllocCallerCallee("nonexistent.etl", ""));
        Assert.Throws<ArgumentException>(() => tools.ClrAllocCallerCallee("nonexistent.etl", "  "));
    }
}
