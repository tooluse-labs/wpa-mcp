using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class CpuAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl"; // captured in Task 17

    [Fact]
    public void CpuTopFunctions_ReturnsAtMostTopRows()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuTopFunctions(FixturePath, top: 10);
        Assert.True(resp.Rows.Count <= 10);
    }

    [Fact]
    public void CpuTopFunctions_RowsOrderedByExclusiveDescending()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuTopFunctions(FixturePath, top: 50);
        for (var i = 1; i < resp.Rows.Count; i++)
            Assert.True(resp.Rows[i - 1].ExclusiveSamples >= resp.Rows[i].ExclusiveSamples);
    }

    [Fact]
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

    [Fact]
    public void CpuCallerCallee_OnNoStackRootReturnsExpectedShape()
    {
        // small_cpu.etl was captured without Sample-stackwalks enabled, so 100% of CPU
        // samples land on the synthetic "?!?" root. Test against that — it's the only
        // frame guaranteed to be present, and exercising it validates the caller/callee
        // mechanics on a sample with no real stack:
        //   focusInclusive == focusExclusive == totalSamples (every sample IS the ?!? leaf)
        //   Callers should contain a single "<root>" entry (?!? was interned with Invalid caller)
        //   Callees should contain a single "<self>" entry (?!? is always the leaf)
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var topResp = tools.CpuTopFunctions(FixturePath, top: 5);
        Assert.Contains(topResp.Rows, r => r.Function == "?!?");
        var noStackRow = topResp.Rows.First(r => r.Function == "?!?");

        var ccResp = tools.CpuCallerCallee(FixturePath, function: "?!?", top: 10);
        Assert.Equal("?!?", ccResp.FocusFunction);
        Assert.Equal("samples", ccResp.MetricName);
        Assert.True(ccResp.FocusInclusiveMetric > 0,
            $"?!? should have inclusive samples > 0; got {ccResp.FocusInclusiveMetric}");
        Assert.Equal(noStackRow.InclusiveSamples, ccResp.FocusInclusiveMetric);
        // ?!? is the leaf of every no-stack sample, so exclusive == inclusive.
        Assert.Equal(ccResp.FocusInclusiveMetric, ccResp.FocusExclusiveMetric);
        Assert.Contains(ccResp.Callers, c => c.Function == "<root>");
        Assert.Contains(ccResp.Callees, c => c.Function == "<self>");
    }

    [Fact]
    public void CpuCallerCallee_UnknownFunctionEmitsWarning()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuCallerCallee(FixturePath, function: "this::is::not::a::real::frame", top: 10);
        Assert.Equal(0, resp.FocusInclusiveMetric);
        Assert.Empty(resp.Callers);
        Assert.Empty(resp.Callees);
        Assert.Contains(resp.Warnings, w => w.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuCallerCallee_RejectsBadInput()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.CpuCallerCallee("nonexistent.etl", function: "x", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.CpuCallerCallee("nonexistent.etl", function: "x", top: 1001));
        Assert.Throws<ArgumentException>(() =>
            tools.CpuCallerCallee("nonexistent.etl", function: "", top: 10));
        Assert.Throws<ArgumentException>(() =>
            tools.CpuCallerCallee("nonexistent.etl", function: "  ", top: 10));
    }

    [Fact]
    public void CpuCallerCallee_CallersAndCalleesOrderedByInclusiveDesc()
    {
        // small_cpu.etl has only ?!? in the data (no Sample-stackwalks), so this collapses
        // to single-row checks where ordering is trivially descending. The assertion still
        // guards against a regression that produces UNORDERED output on richer traces.
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuCallerCallee(FixturePath, function: "?!?", top: 50);
        for (var i = 1; i < resp.Callers.Count; i++)
            Assert.True(resp.Callers[i - 1].InclusiveMetric >= resp.Callers[i].InclusiveMetric);
        for (var i = 1; i < resp.Callees.Count; i++)
            Assert.True(resp.Callees[i - 1].InclusiveMetric >= resp.Callees[i].InclusiveMetric);
    }
}
