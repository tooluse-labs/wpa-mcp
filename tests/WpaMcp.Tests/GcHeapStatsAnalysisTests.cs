using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class GcHeapStatsAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ClrGcHeapStats_NoMatchingEvents_ReturnsZeroMetricsAndWarns()
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
        var resp = tools.ClrGcHeapStats(FixturePath, pid: 999_999);
        Assert.Equal(999_999, resp.Pid);
        Assert.Empty(resp.Rows);
    }
}
