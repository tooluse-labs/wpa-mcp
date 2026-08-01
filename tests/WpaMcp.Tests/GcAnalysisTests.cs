using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

// small_cpu.etl is captured with the WPR 'CPU' profile, which doesn't include the
// Microsoft-Windows-DotNETRuntime ETW provider — so all CLR-based analyzers should
// return empty rows + a MissingClrKeyword warning.  These smoke tests pin down the
// "no-events shape" so a regression that silently swallows the warning, or that
// produces non-empty rows from a CLR-less trace, would fail.
public class GcAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void Project_ClipsGcAndAssociatedPauseIndependently()
    {
        var process = new ProcessInstanceKey(42, 10);
        var pause = new GcPauseInterval(
            new ClrPauseKey(process, 1),
            StartUs: 95,
            EndUs: 130,
            FullDurationUs: 35);
        var set = new GcIntervalSet(
            Gcs:
            [
                new GcWallWithPauses(
                    new ClrGcKey(process, 1, 4),
                    StartUs: 90,
                    EndUs: 210,
                    FullDurationUs: 120,
                    Generation: 2,
                    Reason: "AllocLarge",
                    Pauses: [pause]),
            ],
            OrphanPauses: [],
            IncompleteEvidence: [],
            UnmatchedGcStartCount: 0,
            UnmatchedGcStopCount: 0,
            UnmatchedSuspendStartCount: 0,
            UnmatchedRestartStopCount: 0,
            InvalidIntervalCount: 0);

        var response = GcAnalysis.Project(set, new TimeWindow(100, 200), pid: 42);
        var row = Assert.Single(response.Events);

        Assert.Equal(120, row.FullDurationUs);
        Assert.Equal(100, row.AccountedDurationUs);
        Assert.Equal(35, row.FullPauseUs);
        Assert.Equal(30, row.AccountedPauseUs);
        Assert.Equal(row.AccountedDurationUs, row.DurationUs);
        Assert.Equal(row.AccountedPauseUs, row.PauseUs);
        Assert.Equal(response.TotalAccountedGcUs, response.TotalGcUs);
        Assert.Equal(response.TotalAccountedPauseUs, response.TotalPauseUs);
        Assert.Equal(1, response.MatchedEventCount);
        Assert.Equal(1, response.MatchedIntervalCount);
        Assert.Contains(response.Warnings,
            warning => warning.StartsWith("time_semantics_v2:", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_ClipsOrphanPauseWithoutCountingItAsGcWallTime()
    {
        var process = new ProcessInstanceKey(42, 10);
        var set = new GcIntervalSet(
            Gcs: [],
            OrphanPauses:
            [
                new GcPauseInterval(
                    new ClrPauseKey(process, 1),
                    StartUs: 90,
                    EndUs: 130,
                    FullDurationUs: 40),
            ],
            IncompleteEvidence: [],
            UnmatchedGcStartCount: 0,
            UnmatchedGcStopCount: 0,
            UnmatchedSuspendStartCount: 0,
            UnmatchedRestartStopCount: 0,
            InvalidIntervalCount: 0);

        var response = GcAnalysis.Project(set, new TimeWindow(100, 120), pid: 42);
        var row = Assert.Single(response.Events);

        Assert.True(row.IsOrphanPause);
        Assert.Equal(-1, row.Generation);
        Assert.Equal(40, row.FullDurationUs);
        Assert.Equal(20, row.AccountedDurationUs);
        Assert.Equal(40, row.FullPauseUs);
        Assert.Equal(20, row.AccountedPauseUs);
        Assert.Equal(0, response.TotalFullGcUs);
        Assert.Equal(0, response.TotalAccountedGcUs);
        Assert.Equal(40, response.TotalFullPauseUs);
        Assert.Equal(20, response.TotalAccountedPauseUs);
        Assert.Equal(0, response.MatchedEventCount);
        Assert.Equal(1, response.MatchedIntervalCount);
    }

    [Fact]
    public void Project_ProcessScopeAggregatesButKeepsReusedPidInstancesSeparate()
    {
        var oldProcess = new ProcessInstanceKey(42, 0);
        var newProcess = new ProcessInstanceKey(42, 100);
        var intervals = new GcIntervalSet(
            Gcs:
            [
                Gc(oldProcess, startUs: 10, endUs: 30, gcCount: 1),
                Gc(newProcess, startUs: 110, endUs: 150, gcCount: 1),
            ],
            OrphanPauses: [],
            IncompleteEvidence: [],
            UnmatchedGcStartCount: 0,
            UnmatchedGcStopCount: 0,
            UnmatchedSuspendStartCount: 0,
            UnmatchedRestartStopCount: 0,
            InvalidIntervalCount: 0);
        var window = new TimeWindow(0, 200);
        var lifetimes = new[]
        {
            new ProcessLifetime(oldProcess, 100, true, true),
            new ProcessLifetime(newProcess, 200, true, false),
        };
        var aggregateScope = ProcessAnalysisScope.Resolve(
            window, pid: 42, processStartUs: null, lifetimes);

        var aggregate = GcAnalysis.Project(
            intervals, window, aggregateScope, sourceEventCount: 4);

        Assert.Equal(2, aggregate.TotalGcCount);
        Assert.Equal(60, aggregate.TotalAccountedGcUs);
        Assert.Equal([0L, 100L], aggregate.Events.Select(row => row.ProcessStartUs));
        Assert.Equal("pid_aggregate", aggregate.ScopeMode);
        Assert.True(aggregate.PidReuseObserved);
        Assert.Equal([oldProcess, newProcess], aggregate.IncludedProcesses);
        Assert.Equal(4, aggregate.MatchedEventCount);
        Assert.Equal(2, aggregate.MatchedIntervalCount);
        Assert.Equal("observed", aggregate.CapabilityStatus);
        Assert.Null(aggregate.NoDataReason);

        var exactScope = ProcessAnalysisScope.Resolve(
            window, pid: 42, processStartUs: 100, lifetimes);
        var exact = GcAnalysis.Project(
            intervals, window, exactScope, sourceEventCount: 4);

        var row = Assert.Single(exact.Events);
        Assert.Equal(newProcess.StartUs, row.ProcessStartUs);
        Assert.Equal(40, exact.TotalAccountedGcUs);
        Assert.Equal(newProcess, exact.SelectedProcess);
        Assert.Equal("single_process", exact.ScopeMode);
        Assert.Equal(2, exact.MatchedEventCount);
        Assert.Equal(1, exact.MatchedIntervalCount);
    }

    [Fact]
    public void Project_MissingExactProcessReturnsStructuredEmptyResponse()
    {
        var process = new ProcessInstanceKey(42, 0);
        var window = new TimeWindow(0, 100);
        var scope = ProcessAnalysisScope.Resolve(
            window,
            pid: 42,
            processStartUs: 50,
            [new ProcessLifetime(process, 100, true, false)]);

        var response = GcAnalysis.Project(
            new GcIntervalSet([], [], [], 0, 0, 0, 0, 0),
            window,
            scope,
            sourceEventCount: 2);

        Assert.Empty(response.Events);
        Assert.Equal("scope_not_found", response.ScopeStatus);
        Assert.Equal("scope_not_found", response.NoDataReason);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal(0, response.MatchedEventCount);
    }

    [Fact]
    public void Project_MatchedEndpointWithoutCompletedIntervalReportsIncompleteIntervalEvidence()
    {
        var process = new ProcessInstanceKey(42, 0);
        var window = new TimeWindow(0, 100);
        var scope = ProcessAnalysisScope.Resolve(
            window,
            pid: 42,
            processStartUs: process.StartUs,
            [new ProcessLifetime(process, 100, true, true)]);

        var response = GcAnalysis.Project(
            new GcIntervalSet([], [], [], 1, 0, 0, 0, 0),
            window,
            scope,
            sourceEventCount: 1,
            matchedSourceEventCount: 1);

        Assert.Empty(response.Events);
        Assert.Equal(1, response.MatchedEventCount);
        Assert.Equal(0, response.MatchedIntervalCount);
        Assert.Equal("observed", response.CapabilityStatus);
        Assert.Equal("no_completed_intervals_in_scope", response.NoDataReason);
    }

    [Fact]
    public void Project_NoMatchedEndpointReportsNoEventsInScope()
    {
        var process = new ProcessInstanceKey(42, 0);
        var window = new TimeWindow(0, 100);
        var scope = ProcessAnalysisScope.Resolve(
            window,
            pid: process.Pid,
            processStartUs: process.StartUs,
            [new ProcessLifetime(process, 100, true, true)]);
        var response = GcAnalysis.Project(
            new GcIntervalSet([], [], [], 1, 0, 0, 0, 0),
            window,
            scope,
            sourceEventCount: 1,
            matchedSourceEventCount: 0);

        Assert.Equal(0, response.MatchedEventCount);
        Assert.Equal("no_events_in_scope", response.NoDataReason);
    }

    [Fact]
    public void GcPairing_SameClrInstanceAndCountCannotCrossProcessLifetime()
    {
        var accumulator = new GcIntervalAccumulator();
        accumulator.AddGcStart(
            new ProcessInstanceKey(42, 0), 1, gcCount: 7,
            timestampUs: 10, generation: 2, reason: "Induced");
        accumulator.AddGcStop(
            new ProcessInstanceKey(42, 100), 1, gcCount: 7,
            timestampUs: 120);

        var result = accumulator.Complete();

        Assert.Empty(result.Gcs);
        Assert.Equal(1, result.UnmatchedGcStartCount);
        Assert.Equal(1, result.UnmatchedGcStopCount);
    }

    [Fact]
    public void ClrGcAnalysis_NoMatchingEvents_ReturnsZeroMetricsAndWarns()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrGcAnalysis(FixturePath);
        Assert.Equal(0, resp.TotalGcCount);
        Assert.Equal(0, resp.Gen0Count);
        Assert.Equal(0, resp.Gen1Count);
        Assert.Equal(0, resp.Gen2Count);
        Assert.Equal(0, resp.TotalGcUs);
        Assert.Equal(0, resp.TotalPauseUs);
        Assert.Equal(0, resp.TotalFullGcUs);
        Assert.Equal(0, resp.TotalAccountedGcUs);
        Assert.Equal(0, resp.TotalFullPauseUs);
        Assert.Equal(0, resp.TotalAccountedPauseUs);
        Assert.Equal("clipped_overlap_v2", resp.AccountingMode);
        Assert.Empty(resp.Events);
        Assert.NotEmpty(resp.Warnings);
        Assert.Contains(resp.Warnings, w => w.Contains("CLR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(resp.Warnings,
            warning => warning.StartsWith("time_semantics_v2:", StringComparison.Ordinal));
    }

    [Fact]
    public void ClrGcAnalysis_WithFilters_StillReturnsCleanShape()
    {
        // Window + pid filters set, but trace has no GC events — verify nothing crashes
        // and the response shape is still well-formed (no NaN, no negative counts).
        var cache = new TraceCache(capacity: 2);
        var trace = cache.Get(FixturePath);
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var tools = new ClrTools(cache);
        var resp = tools.ClrGcAnalysis(
            FixturePath, pid: 999_999, startUs: 0, endUs: traceEndUs);
        Assert.Equal(999_999, resp.Pid);
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

    [Fact]
    public void ClrGcAnalysis_PerfViewGcFixture_PreservesGenerationCountsAndDualTotals()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var response = tools.ClrGcAnalysis("fixtures/perfview_gcevents.etl");

        Assert.NotEmpty(response.Events);
        Assert.Equal(
            response.TotalGcCount,
            response.Gen0Count + response.Gen1Count + response.Gen2Count);
        Assert.Equal(
            response.Events
                .Where(row => !row.IsOrphanPause)
                .Sum(row => row.AccountedDurationUs),
            response.TotalAccountedGcUs);
        Assert.Equal(
            response.Events.Sum(row => row.AccountedPauseUs ?? 0),
            response.TotalAccountedPauseUs);
        Assert.All(response.Events,
            row => Assert.Equal("clipped_overlap_v2", row.AccountingMode));
    }

    private static GcWallWithPauses Gc(
        ProcessInstanceKey process,
        long startUs,
        long endUs,
        int gcCount) =>
        new(
            new ClrGcKey(process, ClrInstanceId: 1, GcCount: gcCount),
            startUs,
            endUs,
            FullDurationUs: endUs - startUs,
            Generation: 2,
            Reason: "Induced",
            Pauses: []);
}
