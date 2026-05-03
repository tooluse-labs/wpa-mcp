using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

// small_cpu.etl is captured with the WPR 'CPU' profile, which doesn't include the
// Microsoft-Windows-DotNETRuntime ETW provider — so all CLR-based analyzers should
// return empty rows + a MissingClrKeyword warning.  These smoke tests pin down the
// "no-events shape" so a regression that silently swallows the warning, or that
// produces non-empty rows from a CLR-less trace, would fail.
public class GcAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ClrGcAnalysis_EmptyTrace_ReturnsEmptyAndWarns()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrGcAnalysis(FixturePath);
        Assert.Equal(0, resp.TotalGcCount);
        Assert.Equal(0, resp.Gen0Count);
        Assert.Equal(0, resp.Gen1Count);
        Assert.Equal(0, resp.Gen2Count);
        Assert.Equal(0, resp.TotalGcUs);
        Assert.Equal(0, resp.TotalPauseUs);
        Assert.Empty(resp.Events);
        Assert.NotEmpty(resp.Warnings);
        Assert.Contains(resp.Warnings, w => w.Contains("CLR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClrGcAnalysis_WithFilters_StillReturnsCleanShape()
    {
        // Window + pid filters set, but trace has no GC events — verify nothing crashes
        // and the response shape is still well-formed (no NaN, no negative counts).
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrGcAnalysis(FixturePath, pid: 99999, startUs: 0, endUs: 1_000_000);
        Assert.Equal(99999, resp.Pid);
        Assert.Equal(0, resp.TotalGcCount);
    }

    [Fact]
    public void ClrGcAnalysis_GenerationCountsSumToTotal()
    {
        // Invariant: Gen0 + Gen1 + Gen2 == TotalGcCount.  Critical: this was broken before
        // the simplify pass 2 fix (TotalGcCount used to include orphan-pause rows).
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrGcAnalysis(FixturePath);
        Assert.Equal(resp.TotalGcCount, resp.Gen0Count + resp.Gen1Count + resp.Gen2Count);
    }
}
