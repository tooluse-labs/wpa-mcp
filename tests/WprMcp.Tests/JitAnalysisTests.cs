using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class JitAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ClrJitAnalysis_EmptyTrace_ReturnsEmptyAndWarns()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrJitAnalysis(FixturePath);
        Assert.Equal(0, resp.TotalMethodsJitted);
        Assert.Equal(0, resp.TotalJitUs);
        Assert.Empty(resp.TopMethods);
        Assert.Contains(resp.Warnings, w => w.Contains("CLR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClrJitAnalysis_RejectsBadTop()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ClrJitAnalysis("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ClrJitAnalysis("nonexistent.etl", top: 1001));
    }
}
