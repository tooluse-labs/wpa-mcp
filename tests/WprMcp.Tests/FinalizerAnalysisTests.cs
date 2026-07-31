using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class FinalizerAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void FinalizerProjection_LeftOverlapChargesOnlyWindowContribution()
    {
        var process = new ProcessInstanceKey(12, 10);
        var pair = new PairedInterval<FinalizerPairKey, FinalizerStartData, FinalizerStopData>(
            new FinalizerPairKey(process, 1),
            90,
            130,
            new FinalizerStartData(),
            new FinalizerStopData(FinalizersRun: 4));

        var response = FinalizerAnalysis.ProjectBatches(
            [pair],
            new TimeWindow(100, 120),
            pid: 12);
        var row = Assert.Single(response.Batches);

        Assert.Equal(40, row.FullDurationUs);
        Assert.Equal(20, row.AccountedDurationUs);
        Assert.Equal(20, row.DurationUs);
        Assert.Equal(20, response.TotalBatchUs);
    }

    [Fact]
    public void FinalizerPairing_ReusedPidCannotConsumeOldProcessStart()
    {
        var oldProcess = new ProcessInstanceKey(12, 10);
        var newProcess = new ProcessInstanceKey(12, 200);
        var accumulator = new IntervalPairAccumulator<
            FinalizerPairKey,
            FinalizerStartData,
            FinalizerStopData>();
        accumulator.AddStart(
            new FinalizerPairKey(oldProcess, 1),
            90,
            new FinalizerStartData());
        accumulator.AddStop(
            new FinalizerPairKey(newProcess, 1),
            230,
            new FinalizerStopData(FinalizersRun: 4));

        var result = accumulator.Complete();

        Assert.Empty(result.Pairs);
        Assert.Single(result.UnmatchedStarts);
        Assert.Single(result.UnmatchedStops);
    }

    [Fact]
    public void ClrFinalizerAnalysis_NoMatchingEvents_ReturnsZeroMetricsAndWarns()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrFinalizerAnalysis(FixturePath);
        Assert.Equal(0, resp.TotalObjectsFinalized);
        Assert.Equal(0, resp.TotalBatchUs);
        Assert.Equal(0, resp.TotalFullBatchUs);
        Assert.Equal(0, resp.TotalAccountedBatchUs);
        Assert.Equal("clipped_overlap_v2", resp.AccountingMode);
        Assert.Empty(resp.Batches);
        Assert.Empty(resp.TopTypes);
        Assert.Contains(resp.Warnings, w => w.Contains("CLR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(resp.Warnings,
            warning => warning.StartsWith("time_semantics_v2:", StringComparison.Ordinal));
    }
}
