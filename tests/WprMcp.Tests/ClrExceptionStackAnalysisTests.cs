using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class ClrExceptionStackAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ClrExceptionTopStacks_NoMatchingEvents_ReturnsZeroMetricsAndWarns()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrExceptionTopStacks(FixturePath);
        Assert.Equal(0, resp.TotalEventCount);
        StackAssertions.AssertRootOnly(resp.Rows, r => r.ExclusiveCount, r => r.InclusiveCount);
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
