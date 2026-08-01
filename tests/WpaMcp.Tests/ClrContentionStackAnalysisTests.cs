using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class ClrContentionStackAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ContentionProjection_ReusedTidCannotConsumeOldGenerationStart()
    {
        var process = new ProcessInstanceKey(12, 10);
        var oldThread = new ThreadInstanceKey(process, 77, 1);
        var newThread = new ThreadInstanceKey(process, 77, 2);
        var accumulator = new IntervalPairAccumulator<
            ThreadInstanceKey,
            ContentionStartData,
            ContentionStopData>();
        accumulator.AddStart(oldThread, 90, new ContentionStartData(default));
        accumulator.AddStop(newThread, 130, new ContentionStopData());

        var result = accumulator.Complete();
        var coverage = TraceCapabilitiesDetector.ProjectClrContentionCoverage(result);

        Assert.Empty(result.Pairs);
        Assert.Single(result.UnmatchedStarts);
        Assert.Single(result.UnmatchedStops);
        Assert.Equal("clr_contention", coverage.Domain);
        Assert.Equal("us", coverage.MetricName);
        Assert.Equal("no_events", coverage.CoverageState);
        Assert.Equal(0, coverage.TotalEventCount);
        Assert.Equal(0, coverage.TotalMetric);
    }

    [Fact]
    public void ContentionProjection_ClipsIntervalAndPreservesFullDuration()
    {
        var process = new ProcessInstanceKey(12, 10);
        var thread = new ThreadInstanceKey(process, 77, 1);
        var pair = new PairedInterval<
            ThreadInstanceKey,
            ContentionStartData,
            ContentionStopData>(
            thread,
            90,
            130,
            new ContentionStartData(default),
            new ContentionStopData());
        var scope = new ThreadAnalysisScope(
            new TimeWindow(100, 120),
            Pid: null,
            Process: null,
            Thread: null,
            AggregatesPidLifetimes: false,
            PidReuseObserved: false);

        var projection = ClrContentionStackAnalysis.ProjectIntervals(
            [pair],
            scope,
            unmatchedIntervalCount: 0,
            invalidIntervalCount: 0);
        var sample = Assert.Single(projection.Samples);

        Assert.Equal(40, sample.FullDurationUs);
        Assert.Equal(20, sample.AccountedDurationUs);
        Assert.Equal(40, projection.TotalFullDurationUs);
        Assert.Equal(20, projection.TotalAccountedDurationUs);
        Assert.Equal("clipped_overlap_v2", sample.AccountingMode);
    }

    [Fact]
    public void ClrContentionTopStacks_NoMatchingEvents_ReturnsZeroMetricsAndWarns()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrContentionTopStacks(FixturePath);
        Assert.Equal(0, resp.TotalBlockedUs);
        Assert.Equal(0, resp.TotalFullBlockedUs);
        Assert.Equal(0, resp.TotalAccountedBlockedUs);
        Assert.Equal("clipped_overlap_v2", resp.AccountingMode);
        Assert.Equal(0, resp.TotalEventCount);
        Assert.Empty(resp.Rows);
        Assert.All(resp.Rows, row =>
        {
            Assert.Equal(row.ExclusiveBlockedUs, row.ExclusiveAccountedBlockedUs);
            Assert.Equal(row.InclusiveBlockedUs, row.InclusiveAccountedBlockedUs);
            Assert.Equal("clipped_overlap_v2", row.AccountingMode);
        });
        Assert.Contains(resp.Warnings, w => w.Contains("CLR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(resp.Warnings,
            warning => warning.StartsWith("time_semantics_v2:", StringComparison.Ordinal));
    }

    [Fact]
    public void ClrContentionTopStacks_PidFilter_DoesNotCrossContaminate()
    {
        // Regression guard: post-/simplify pass 3, pendingByTid is keyed on (pid, tid)
        // not just tid.  Hard to exercise without a real CLR trace, but at minimum verify
        // pid-filtered analysis returns a clean shape without throwing or producing
        // metric-bearing rows.
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrContentionTopStacks(FixturePath, pid: 4); // System
        Assert.Equal(0, resp.TotalEventCount);
        Assert.Empty(resp.Rows);
    }

    [Fact]
    public void ClrContentionTopStacks_RejectsBadInput()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ClrContentionTopStacks("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ClrContentionTopStacks("nonexistent.etl", top: 1001));
    }

    [Fact]
    public void ClrContentionCallerCallee_RejectsEmptyFunction()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentException>(() => tools.ClrContentionCallerCallee("nonexistent.etl", ""));
    }
}
