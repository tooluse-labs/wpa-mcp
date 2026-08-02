using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

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
        Assert.Equal(2, response.MatchedEventCount);
        Assert.Equal(2, response.MatchedIntervalCount);
        Assert.True(
            response.TopMethods.Sum(row => row.AccountedDurationUs) <=
            response.TotalAccountedJitUs);
    }

    [Fact]
    public void JitProjection_ProcessScopeAggregatesAndSelectsExactInstance()
    {
        var oldProcess = new ProcessInstanceKey(8, 0);
        var newProcess = new ProcessInstanceKey(8, 100);
        var pairs = new[]
        {
            Pair(oldProcess, 10, 30),
            Pair(newProcess, 110, 150),
        };
        var window = new TimeWindow(0, 200);
        var lifetimes = new[]
        {
            new ProcessLifetime(oldProcess, 100, true, true),
            new ProcessLifetime(newProcess, 200, true, false),
        };
        var aggregateScope = ProcessAnalysisScope.Resolve(
            window, pid: 8, processStartUs: null, lifetimes);

        var aggregate = JitAnalysis.ProjectPairs(
            pairs, window, aggregateScope, top: 10, sourceEventCount: 4);

        Assert.Equal(2, aggregate.TotalMethodsJitted);
        Assert.Equal(60, aggregate.TotalAccountedJitUs);
        Assert.Equal([0L, 100L], aggregate.TopMethods
            .OrderBy(row => row.ProcessStartUs)
            .Select(row => row.ProcessStartUs));
        Assert.Equal("pid_aggregate", aggregate.ScopeMode);
        Assert.True(aggregate.PidReuseObserved);
        Assert.Equal([oldProcess, newProcess], aggregate.IncludedProcesses);
        Assert.Equal(4, aggregate.MatchedEventCount);
        Assert.Equal(2, aggregate.MatchedIntervalCount);

        var exactScope = ProcessAnalysisScope.Resolve(
            window, pid: 8, processStartUs: 100, lifetimes);
        var exact = JitAnalysis.ProjectPairs(
            pairs, window, exactScope, top: 10, sourceEventCount: 4);

        Assert.Equal(newProcess.StartUs, Assert.Single(exact.TopMethods).ProcessStartUs);
        Assert.Equal(newProcess, exact.SelectedProcess);
        Assert.Equal("single_process", exact.ScopeMode);
        Assert.Equal(40, exact.TotalAccountedJitUs);
        Assert.Equal(2, exact.MatchedEventCount);
        Assert.Equal(1, exact.MatchedIntervalCount);
    }

    [Fact]
    public void JitProjection_MissingExactProcessReturnsStructuredEmptyResponse()
    {
        var process = new ProcessInstanceKey(8, 0);
        var window = new TimeWindow(0, 100);
        var scope = ProcessAnalysisScope.Resolve(
            window,
            pid: 8,
            processStartUs: 50,
            [new ProcessLifetime(process, 100, true, false)]);

        var response = JitAnalysis.ProjectPairs(
            [], window, scope, top: 10, sourceEventCount: 2);

        Assert.Empty(response.TopMethods);
        Assert.Equal("scope_not_found", response.ScopeStatus);
        Assert.Equal("scope_not_found", response.NoDataReason);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal(0, response.MatchedEventCount);
    }

    [Fact]
    public void JitProjection_MatchedEndpointWithoutCompletedIntervalReportsIncompleteIntervalEvidence()
    {
        var process = new ProcessInstanceKey(8, 0);
        var window = new TimeWindow(0, 100);
        var scope = ProcessAnalysisScope.Resolve(
            window,
            pid: 8,
            processStartUs: process.StartUs,
            [new ProcessLifetime(process, 100, true, true)]);

        var response = JitAnalysis.ProjectPairs(
            [],
            window,
            scope,
            top: 10,
            sourceEventCount: 1,
            matchedSourceEventCount: 1,
            unmatchedIntervalCount: 1);

        Assert.Empty(response.TopMethods);
        Assert.Equal(1, response.MatchedEventCount);
        Assert.Equal(0, response.MatchedIntervalCount);
        Assert.Equal("observed", response.CapabilityStatus);
        Assert.Equal("no_completed_intervals_in_scope", response.NoDataReason);
    }

    [Fact]
    public void JitProjection_NoMatchedEndpointReportsNoEventsInScope()
    {
        var process = new ProcessInstanceKey(8, 0);
        var window = new TimeWindow(0, 100);
        var scope = ProcessAnalysisScope.Resolve(
            window,
            pid: process.Pid,
            processStartUs: process.StartUs,
            [new ProcessLifetime(process, 100, true, true)]);
        var response = JitAnalysis.ProjectPairs(
            [],
            window,
            scope,
            top: 10,
            sourceEventCount: 1,
            matchedSourceEventCount: 0,
            unmatchedIntervalCount: 1);

        Assert.Equal(0, response.MatchedEventCount);
        Assert.Equal("no_events_in_scope", response.NoDataReason);
    }

    [Fact]
    public void JitProjection_IdentityDroppedEndpointReportsUnattributedSource()
    {
        var process = new ProcessInstanceKey(8, 0);
        var window = new TimeWindow(0, 100);
        var scope = ProcessAnalysisScope.Resolve(
            window,
            pid: process.Pid,
            processStartUs: process.StartUs,
            [new ProcessLifetime(process, 100, true, true)]);

        var response = JitAnalysis.ProjectPairs(
            [],
            window,
            scope,
            top: 10,
            sourceEventCount: 1,
            matchedSourceEventCount: 1,
            traceIdentityUnresolvedEndpointCount: 1,
            scopedIdentityUnresolvedEndpointCount: 1);

        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal("source_events_unattributed", response.NoDataReason);
        Assert.Equal(1, response.TraceIdentityUnresolvedEndpointCount);
        Assert.Equal(1, response.ScopedIdentityUnresolvedEndpointCount);
    }

    [Fact]
    public void JitProjection_SeparatesTraceAndScopedIntervalAnomalies()
    {
        var process = new ProcessInstanceKey(8, 0);
        var window = new TimeWindow(0, 100);
        var scope = ProcessAnalysisScope.Resolve(
            window,
            pid: process.Pid,
            processStartUs: process.StartUs,
            [new ProcessLifetime(process, 100, true, true)]);

        var response = JitAnalysis.ProjectPairs(
            [],
            window,
            scope,
            top: 10,
            sourceEventCount: 7,
            unmatchedIntervalCount: 3,
            invalidIntervalCount: 2,
            scopedUnmatchedIntervalCount: 1,
            scopedInvalidIntervalCount: 1,
            traceUnmatchedStartCount: 2,
            traceUnmatchedStopCount: 1,
            scopedUnmatchedStartCount: 1,
            scopedUnmatchedStopCount: 0);

        Assert.Equal(3, response.TraceUnmatchedIntervalCount);
        Assert.Equal(1, response.ScopedUnmatchedIntervalCount);
        Assert.Equal(2, response.TraceInvalidIntervalCount);
        Assert.Equal(1, response.ScopedInvalidIntervalCount);
        Assert.Equal(2, response.TraceUnmatchedStartCount);
        Assert.Equal(1, response.TraceUnmatchedStopCount);
        Assert.Equal(1, response.ScopedUnmatchedStartCount);
        Assert.Equal(0, response.ScopedUnmatchedStopCount);
    }

    [Fact]
    public void JitPairing_SameMethodAndClrInstanceCannotCrossProcessLifetime()
    {
        var accumulator = new IntervalPairAccumulator<
            JitPairKey,
            JitStartData,
            JitStopData>();
        accumulator.AddStart(
            new JitPairKey(new ProcessInstanceKey(8, 0), 1, MethodId: 7),
            10,
            new JitStartData("M", 10));
        accumulator.AddStop(
            new JitPairKey(new ProcessInstanceKey(8, 100), 1, MethodId: 7),
            120,
            new JitStopData());

        var result = accumulator.Complete();

        Assert.Empty(result.Pairs);
        Assert.Single(result.UnmatchedStarts);
        Assert.Single(result.UnmatchedStops);
    }

    [Fact]
    public void JitRows_UseCompleteStableTieBreakersForEqualAccountedDuration()
    {
        static PairedInterval<JitPairKey, JitStartData, JitStopData> RankedPair(
            int pid,
            long processStartUs,
            long methodId,
            string method,
            int ilSize,
            long endUs) =>
            new(
                new JitPairKey(
                    new ProcessInstanceKey(pid, processStartUs),
                    ClrInstanceId: 1,
                    MethodId: methodId),
                StartUs: 90,
                EndUs: endUs,
                new JitStartData(method, ilSize),
                new JitStopData());

        var response = JitAnalysis.ProjectPairs(
            [
                RankedPair(20, 200, 7, "A", 10, 160),
                RankedPair(10, 300, 6, "A", 10, 160),
                RankedPair(10, 100, 5, "B", 1, 155),
                RankedPair(10, 100, 4, "A", 5, 170),
                RankedPair(10, 100, 3, "A", 20, 160),
                RankedPair(10, 100, 2, "A", 10, 160),
            ],
            new TimeWindow(100, 150),
            pid: null,
            top: 10);

        Assert.All(response.TopMethods, row => Assert.Equal(50, row.AccountedDurationUs));
        Assert.Equal(
            new[]
            {
                (10, 100L, "A", 160L, 10),
                (10, 100L, "A", 160L, 20),
                (10, 100L, "A", 170L, 5),
                (10, 100L, "B", 155L, 1),
                (10, 300L, "A", 160L, 10),
                (20, 200L, "A", 160L, 10),
            },
            response.TopMethods.Select(row =>
                (row.Pid, row.ProcessStartUs, row.Method, row.EndUs, row.MethodIlSize)));
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

    private static PairedInterval<JitPairKey, JitStartData, JitStopData> Pair(
        ProcessInstanceKey process,
        long startUs,
        long endUs) =>
        new(
            new JitPairKey(process, ClrInstanceId: 1, MethodId: 7),
            startUs,
            endUs,
            new JitStartData("M", 10),
            new JitStopData());
}
