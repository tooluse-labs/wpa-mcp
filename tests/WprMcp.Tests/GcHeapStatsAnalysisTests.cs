using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class GcHeapStatsAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ClrGcHeapStats_EmptyTrace_ReturnsEmptyAndWarns()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrGcHeapStats(FixturePath);
        Assert.Empty(resp.Rows);
        Assert.Contains(resp.Warnings, w => w.Contains("CLR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClrGcHeapStats_PidFilterPropagates()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrGcHeapStats(FixturePath, pid: 12345);
        Assert.Equal(12345, resp.Pid);
        Assert.Empty(resp.Rows);
    }
}
