using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class JitAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void JitProjection_AccountsAllCompletedRowsBeforeTop()
    {
        var process = new ProcessInstanceKey(8, 0);
        var pairs = new[]
        {
            new PairedInterval<JitPairKey, JitStartData, JitStopData>(
                new JitPairKey(process, 1, 1),
                90,
                130,
                new JitStartData("A", 10),
                new JitStopData()),
            new PairedInterval<JitPairKey, JitStartData, JitStopData>(
                new JitPairKey(process, 1, 2),
                120,
                180,
                new JitStartData("B", 20),
                new JitStopData()),
        };

        var response = JitAnalysis.ProjectPairs(
            pairs,
            new TimeWindow(100, 150),
            pid: 8,
            top: 1);

        Assert.Equal(60, response.TotalAccountedJitUs);
        Assert.Equal(response.TotalAccountedJitUs, response.TotalJitUs);
        Assert.Single(response.TopMethods);
        Assert.True(response.HasMore);
        Assert.True(
            response.TopMethods.Sum(row => row.AccountedDurationUs) <=
            response.TotalAccountedJitUs);
    }

    [Fact]
    public void ClrJitAnalysis_NoMatchingEvents_ReturnsZeroMetricsAndWarns()
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
