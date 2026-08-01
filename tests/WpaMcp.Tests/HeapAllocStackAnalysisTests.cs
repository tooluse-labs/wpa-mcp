using WpaMcp.Core;
using WpaMcp.Output;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class HeapAllocStackAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void HeapAllocTopStacks_NoHeapTrace_EmitsPerProcessWarning()
    {
        // small_cpu.etl was captured without /HeapTrace; verify the analyzer routes through
        // WarningBuilder.MissingPerProcessHeapTrace by const-reference equality.
        var tools = new HeapTools(new TraceCache(capacity: 2));
        var resp = tools.HeapAllocTopStacks(FixturePath);
        Assert.Equal(0, resp.TotalBytes);
        Assert.Equal(0, resp.TotalEventCount);
        Assert.Equal(0, resp.AllocBytes);
        Assert.Equal(0, resp.ReallocBytes);
        Assert.Empty(resp.Rows);
        Assert.Contains(WarningBuilder.MissingPerProcessHeapTrace, resp.Warnings);
    }

    [Fact]
    public void HeapAllocTopStacks_RejectsBadInput()
    {
        var tools = new HeapTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.HeapAllocTopStacks("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.HeapAllocTopStacks("nonexistent.etl", top: 1001));
    }

    [Fact]
    public void HeapAllocCallerCallee_RejectsEmptyFunction()
    {
        var tools = new HeapTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentException>(() => tools.HeapAllocCallerCallee("nonexistent.etl", ""));
    }
}
