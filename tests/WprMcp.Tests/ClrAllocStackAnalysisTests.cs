using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class ClrAllocStackAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ClrAllocTopStacks_EmptyTrace_ReturnsEmptyAndWarns()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrAllocTopStacks(FixturePath);
        // CallTree emits a synthetic ROOT node even when the stack source is empty —
        // assert via metric totals + warning rather than row count.
        Assert.Equal(0, resp.TotalBytes);
        Assert.Equal(0, resp.TotalEventCount);
        Assert.All(resp.Rows, r => Assert.Equal(0, r.ExclusiveBytes));
        Assert.Empty(resp.TopTypes);
        Assert.Contains(resp.Warnings, w => w.Contains("CLR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClrAllocCallerCallee_EmptyTrace_NoFocusMatch()
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
