using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class ClrExceptionStackAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ClrExceptionTopStacks_EmptyTrace_ReturnsEmptyAndWarns()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrExceptionTopStacks(FixturePath);
        // CallTree emits a synthetic ROOT node even when the stack source is empty.
        Assert.Equal(0, resp.TotalEventCount);
        Assert.All(resp.Rows, r => Assert.Equal(0, r.ExclusiveCount));
        Assert.Empty(resp.TopTypes);
        Assert.Contains(resp.Warnings, w => w.Contains("CLR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClrExceptionTopStacks_RejectsBadTop()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ClrExceptionTopStacks("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ClrExceptionTopStacks("nonexistent.etl", top: 1001));
    }

    [Fact]
    public void ClrExceptionCallerCallee_RejectsEmptyFunction()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentException>(() => tools.ClrExceptionCallerCallee("nonexistent.etl", ""));
    }
}
