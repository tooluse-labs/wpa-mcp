using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class CpuAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl"; // captured in Task 17

    [Fact(Skip = "Requires fixtures/small_cpu.etl from Task 17 capture")]
    public void CpuTopFunctions_ReturnsAtMostTopRows()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuTopFunctions(FixturePath, top: 10);
        Assert.True(resp.Rows.Count <= 10);
    }

    [Fact(Skip = "Requires fixtures/small_cpu.etl from Task 17 capture")]
    public void CpuTopFunctions_RowsOrderedByExclusiveDescending()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuTopFunctions(FixturePath, top: 50);
        for (var i = 1; i < resp.Rows.Count; i++)
            Assert.True(resp.Rows[i - 1].ExclusiveSamples >= resp.Rows[i].ExclusiveSamples);
    }

    [Fact(Skip = "Requires fixtures/small_cpu.etl from Task 17 capture")]
    public void CpuTopFunctions_EmitsResolutionStats()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuTopFunctions(FixturePath, top: 10);
        Assert.True(resp.Stats.ResolutionRate >= 0.0 && resp.Stats.ResolutionRate <= 1.0);
    }

    [Fact]
    public void CpuTopFunctions_RejectsBadTop()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.CpuTopFunctions("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.CpuTopFunctions("nonexistent.etl", top: 1001));
    }
}
