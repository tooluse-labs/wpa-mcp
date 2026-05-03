using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class ClrContentionStackAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ClrContentionTopStacks_EmptyTrace_ReturnsEmptyAndWarns()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrContentionTopStacks(FixturePath);
        // CallTree emits a synthetic ROOT node even when the stack source is empty.
        Assert.Equal(0, resp.TotalBlockedUs);
        Assert.Equal(0, resp.TotalEventCount);
        Assert.All(resp.Rows, r => Assert.Equal(0, r.ExclusiveBlockedUs));
        Assert.Contains(resp.Warnings, w => w.Contains("CLR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClrContentionTopStacks_PidFilter_DoesNotCrossContaminate()
    {
        // Regression guard: post-/simplify pass 3, pendingByTid is keyed on (pid, tid)
        // not just tid.  Hard to exercise without a real CLR trace, but at minimum verify
        // pid-filtered analysis returns a clean shape without throwing or producing
        // metric-bearing rows.
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrContentionTopStacks(FixturePath, pid: 4); // System
        Assert.Equal(0, resp.TotalEventCount);
        Assert.All(resp.Rows, r => Assert.Equal(0, r.ExclusiveBlockedUs));
    }

    [Fact]
    public void ClrContentionTopStacks_RejectsBadInput()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ClrContentionTopStacks("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ClrContentionTopStacks("nonexistent.etl", top: 1001));
    }

    [Fact]
    public void ClrContentionCallerCallee_RejectsEmptyFunction()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentException>(() => tools.ClrContentionCallerCallee("nonexistent.etl", ""));
    }
}
