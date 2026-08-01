using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

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
        Assert.Equal(0, response.MatchedEventCount);
        Assert.Equal(1, response.MatchedBatchCount);
        Assert.Equal(0, response.MatchedBatchEndpointEventCount);
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
    public void AnalyzeEvents_AggregateAndExactScopesKeepBatchesAndTopTypesIsolated()
    {
        var oldProcess = new ProcessInstanceKey(12, 0);
        var newProcess = new ProcessInstanceKey(12, 100);
        var lifetimes = new[]
        {
            new ProcessLifetime(oldProcess, 100, true, true),
            new ProcessLifetime(newProcess, 200, true, false),
        };
        var events = new[]
        {
            FinalizerEvent.Object(12, 10, "Old.Type"),
            FinalizerEvent.BatchStart(12, 20, clrInstanceId: 1),
            FinalizerEvent.BatchStop(12, 40, clrInstanceId: 1, count: 1),
            FinalizerEvent.Object(12, 110, "New.Type"),
            FinalizerEvent.BatchStart(12, 120, clrInstanceId: 1),
            FinalizerEvent.BatchStop(12, 160, clrInstanceId: 1, count: 1),
        };
        var window = new TimeWindow(0, 200);

        var aggregate = FinalizerAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes: lifetimes,
            events,
            pid: 12,
            window,
            processStartUs: null);

        Assert.Equal(2, aggregate.TotalObjectsFinalized);
        Assert.Equal(2, aggregate.Batches.Count);
        Assert.Equal(60, aggregate.TotalAccountedBatchUs);
        Assert.Equal(["New.Type", "Old.Type"], aggregate.TopTypes
            .Select(row => row.TypeName).Order());
        Assert.Equal([0L, 100L], aggregate.Batches.Select(row => row.ProcessStartUs));
        Assert.Equal("pid_aggregate", aggregate.ScopeMode);
        Assert.True(aggregate.PidReuseObserved);
        Assert.Equal([oldProcess, newProcess], aggregate.IncludedProcesses);
        Assert.Equal(6, aggregate.MatchedEventCount);
        Assert.Equal(2, aggregate.MatchedObjectEventCount);
        Assert.Equal(4, aggregate.MatchedBatchEndpointEventCount);
        Assert.Equal(2, aggregate.MatchedBatchCount);

        var exact = FinalizerAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes: lifetimes,
            events,
            pid: 12,
            window,
            processStartUs: newProcess.StartUs);

        Assert.Equal(1, exact.TotalObjectsFinalized);
        Assert.Equal("New.Type", Assert.Single(exact.TopTypes).TypeName);
        Assert.Equal(newProcess.StartUs, Assert.Single(exact.Batches).ProcessStartUs);
        Assert.Equal(newProcess, exact.SelectedProcess);
        Assert.Equal("single_process", exact.ScopeMode);
        Assert.Equal(3, exact.MatchedEventCount);
        Assert.Equal(1, exact.MatchedObjectEventCount);
        Assert.Equal(2, exact.MatchedBatchEndpointEventCount);
        Assert.Equal(1, exact.MatchedBatchCount);
    }

    [Fact]
    public void AnalyzeEvents_MissingExactProcessReturnsStructuredEmptyResponse()
    {
        var process = new ProcessInstanceKey(12, 0);
        var response = FinalizerAnalysis.AnalyzeEvents(
            traceEndUs: 100,
            processLifetimes:
            [
                new ProcessLifetime(process, 100, true, false),
            ],
            events: [],
            pid: 12,
            window: new TimeWindow(0, 100),
            processStartUs: 50);

        Assert.Empty(response.Batches);
        Assert.Empty(response.TopTypes);
        Assert.Equal("scope_not_found", response.ScopeStatus);
        Assert.Equal("scope_not_found", response.NoDataReason);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal(0, response.MatchedEventCount);
    }

    [Fact]
    public void AnalyzeEvents_MatchedEndpointWithoutCompletedBatchReportsIncompleteIntervalEvidence()
    {
        var process = new ProcessInstanceKey(12, 0);
        var response = FinalizerAnalysis.AnalyzeEvents(
            traceEndUs: 100,
            processLifetimes:
            [
                new ProcessLifetime(process, 100, true, true),
            ],
            events:
            [
                FinalizerEvent.BatchStart(
                    process.Pid,
                    timeUs: 20,
                    clrInstanceId: 1),
            ],
            pid: process.Pid,
            window: new TimeWindow(0, 100),
            processStartUs: process.StartUs);

        Assert.Empty(response.Batches);
        Assert.Equal(1, response.MatchedEventCount);
        Assert.Equal(1, response.MatchedBatchEndpointEventCount);
        Assert.Equal(0, response.MatchedBatchCount);
        Assert.Equal("observed", response.CapabilityStatus);
        Assert.Equal("no_completed_intervals_in_scope", response.NoDataReason);
    }

    [Fact]
    public void AnalyzeEvents_NoMatchedEndpointReportsNoEventsInScope()
    {
        var process = new ProcessInstanceKey(12, 0);
        var response = FinalizerAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes:
            [
                new ProcessLifetime(process, 200, true, true),
            ],
            events:
            [
                FinalizerEvent.BatchStart(
                    process.Pid,
                    timeUs: 120,
                    clrInstanceId: 1),
            ],
            pid: process.Pid,
            window: new TimeWindow(0, 100),
            processStartUs: process.StartUs);

        Assert.Equal(0, response.MatchedEventCount);
        Assert.Equal("no_events_in_scope", response.NoDataReason);
    }

    [Fact]
    public void AnalyzeEvents_EventOutsideSelectedLifetimeIsNotScopedUnattributed()
    {
        var process = new ProcessInstanceKey(12, 0);
        var response = FinalizerAnalysis.AnalyzeEvents(
            traceEndUs: 100,
            processLifetimes:
            [
                new ProcessLifetime(process, 50, true, true),
            ],
            events:
            [
                FinalizerEvent.BatchStart(
                    process.Pid,
                    timeUs: 75,
                    clrInstanceId: 1),
            ],
            pid: process.Pid,
            window: new TimeWindow(0, 100),
            processStartUs: process.StartUs);

        Assert.Empty(response.Batches);
        Assert.Equal(0, response.MatchedEventCount);
        Assert.Equal("no_events_in_scope", response.NoDataReason);
        Assert.Equal(1, response.TraceIdentityUnresolvedEventCount);
        Assert.Equal(0, response.ScopedIdentityUnresolvedEventCount);
        Assert.DoesNotContain(response.Warnings, warning =>
            warning.StartsWith("source_events_unattributed:", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeEvents_AllProcessUnresolvedObjectIsUnattributedButNotInterval()
    {
        var response = FinalizerAnalysis.AnalyzeEvents(
            traceEndUs: 100,
            processLifetimes: [],
            events: [FinalizerEvent.Object(12, 25, "Missing.Identity")],
            pid: null,
            window: new TimeWindow(0, 100),
            processStartUs: null);

        Assert.Equal("source_events_unattributed", response.NoDataReason);
        Assert.Equal(1, response.TraceIdentityUnresolvedEventCount);
        Assert.Equal(1, response.ScopedIdentityUnresolvedEventCount);
        Assert.Equal(0, response.UnmatchedIntervalCount);
    }

    [Fact]
    public void AnalyzeEvents_MissingClrIdentityInSiblingLifetimeIsNotScoped()
    {
        var selected = new ProcessInstanceKey(12, 50);
        var response = FinalizerAnalysis.AnalyzeEvents(
            traceEndUs: 100,
            processLifetimes:
            [
                new ProcessLifetime(new ProcessInstanceKey(12, 0), 50, true, true),
                new ProcessLifetime(selected, 100, true, false),
            ],
            events: [FinalizerEvent.BatchStart(12, 25, clrInstanceId: null)],
            pid: 12,
            window: new TimeWindow(0, 100),
            processStartUs: selected.StartUs);

        Assert.Equal("no_events_in_scope", response.NoDataReason);
        Assert.Equal(1, response.TraceIdentityUnresolvedEventCount);
        Assert.Equal(0, response.ScopedIdentityUnresolvedEventCount);
        Assert.Equal(0, response.UnmatchedIntervalCount);
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
