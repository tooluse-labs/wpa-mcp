using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class GcHeapStatsAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ClrGcHeapStats_NoMatchingEvents_ReturnsZeroMetricsAndWarns()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrGcHeapStats(FixturePath);
        Assert.Empty(resp.Rows);
        Assert.Contains(resp.Warnings, w => w.Contains("CLR", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ok", resp.ScopeStatus);
        Assert.Equal("not_observed", resp.CapabilityStatus);
        Assert.Equal("event_class_not_observed", resp.NoDataReason);
        Assert.Equal(0, resp.MatchedEventCount);
    }

    [Fact]
    public void ClrGcHeapStats_PidFilterPropagates()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrGcHeapStats(FixturePath, pid: 999_999);
        Assert.Equal(999_999, resp.Pid);
        Assert.Empty(resp.Rows);
        Assert.Equal("scope_not_found", resp.ScopeStatus);
        Assert.Equal("unknown", resp.CapabilityStatus);
        Assert.Equal("scope_not_found", resp.NoDataReason);
    }

    [Fact]
    public void ClrGcHeapStats_ProcessStartRequiresPidBeforeTraceAccess()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentException>(() =>
            tools.ClrGcHeapStats("nonexistent.etl", processStartUs: 10));
    }

    [Fact]
    public void AnalyzeEvents_AllProcessScopeKeepsReusedPidTimelinesSeparate()
    {
        var oldProcess = new ProcessInstanceKey(10, 0);
        var newProcess = new ProcessInstanceKey(10, 100);
        var response = GcHeapStatsAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes:
            [
                new ProcessLifetime(oldProcess, 100, true, true),
                new ProcessLifetime(newProcess, 200, true, false),
            ],
            events:
            [
                HeapStats(10, timeUs: 20, heapBytes: 1_000),
                HeapStats(10, timeUs: 120, heapBytes: 2_000),
            ],
            pid: null,
            window: new TimeWindow(0, 200),
            processStartUs: null);

        Assert.Equal(2, response.Rows.Count);
        Assert.Equal(
            [(0L, 1_000L), (100L, 2_000L)],
            response.Rows.Select(row => (row.ProcessStartUs, row.TotalHeapBytes)));
        Assert.Equal("all_processes", response.ScopeMode);
        Assert.True(response.PidReuseObserved);
        Assert.Equal([oldProcess, newProcess], response.IncludedProcesses);
        Assert.Equal(2, response.MatchedEventCount);
        Assert.Equal("observed", response.CapabilityStatus);
        Assert.Null(response.NoDataReason);
    }

    [Fact]
    public void AnalyzeEvents_ReusedPidWithoutStartReturnsStructuredSelectorFailure()
    {
        var response = GcHeapStatsAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes:
            [
                new ProcessLifetime(new ProcessInstanceKey(10, 0), 100, true, true),
                new ProcessLifetime(new ProcessInstanceKey(10, 100), 200, true, false),
            ],
            events: [],
            pid: 10,
            window: new TimeWindow(0, 200),
            processStartUs: null);

        Assert.Empty(response.Rows);
        Assert.Equal("unresolved", response.ScopeMode);
        Assert.Equal("process_start_required", response.ScopeStatus);
        Assert.Equal("process_start_required", response.NoDataReason);
        Assert.Equal(
            [new ProcessInstanceKey(10, 0), new ProcessInstanceKey(10, 100)],
            response.IncludedProcesses);
        Assert.Contains(response.Warnings, warning =>
            warning.StartsWith("process_start_required:", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeEvents_ReusedPidWindowIntersectingOneLifetimeDoesNotRequireSelector()
    {
        var selected = new ProcessInstanceKey(10, 100);
        var response = GcHeapStatsAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes:
            [
                new ProcessLifetime(new ProcessInstanceKey(10, 0), 100, true, true),
                new ProcessLifetime(selected, 200, true, false),
            ],
            events: [HeapStats(10, timeUs: 120, heapBytes: 2_000)],
            pid: 10,
            window: new TimeWindow(100, 200),
            processStartUs: null);

        var row = Assert.Single(response.Rows);
        Assert.Equal(selected.StartUs, row.ProcessStartUs);
        Assert.Equal("single_process", response.ScopeMode);
        Assert.Equal("ok", response.ScopeStatus);
        Assert.True(response.PidReuseObserved);
    }

    [Fact]
    public void AnalyzeEvents_EventOutsideSelectedLifetimeIsNotScopedUnattributed()
    {
        var response = GcHeapStatsAnalysis.AnalyzeEvents(
            traceEndUs: 100,
            processLifetimes:
            [
                new ProcessLifetime(
                    new ProcessInstanceKey(10, 0), 50, true, true),
            ],
            events: [HeapStats(10, timeUs: 75, heapBytes: 2_000)],
            pid: 10,
            window: new TimeWindow(0, 100),
            processStartUs: 0);

        Assert.Empty(response.Rows);
        Assert.Equal("no_events_in_scope", response.NoDataReason);
        Assert.Equal(1, response.TraceIdentityUnresolvedEventCount);
        Assert.Equal(0, response.ScopedIdentityUnresolvedEventCount);
        Assert.DoesNotContain(response.Warnings, warning =>
            warning.StartsWith("source_events_unattributed:", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeEvents_AllProcessRawEventWithoutIdentityReportsUnattributed()
    {
        var response = GcHeapStatsAnalysis.AnalyzeEvents(
            traceEndUs: 100,
            processLifetimes: [],
            events: [HeapStats(10, timeUs: 75, heapBytes: 2_000)],
            pid: null,
            window: new TimeWindow(0, 100),
            processStartUs: null);

        Assert.Empty(response.Rows);
        Assert.Equal("source_events_unattributed", response.NoDataReason);
        Assert.Equal(1, response.TraceIdentityUnresolvedEventCount);
        Assert.Equal(1, response.ScopedIdentityUnresolvedEventCount);
        Assert.Contains(response.Warnings, warning =>
            warning.StartsWith("source_events_unattributed:", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeEvents_ExactSelectorReturnsOnlySelectedTimeline()
    {
        var selected = new ProcessInstanceKey(10, 100);
        var response = GcHeapStatsAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes:
            [
                new ProcessLifetime(new ProcessInstanceKey(10, 0), 100, true, true),
                new ProcessLifetime(selected, 200, true, false),
            ],
            events:
            [
                HeapStats(10, timeUs: 20, heapBytes: 1_000),
                HeapStats(10, timeUs: 120, heapBytes: 2_000),
            ],
            pid: 10,
            window: new TimeWindow(0, 200),
            processStartUs: selected.StartUs);

        var row = Assert.Single(response.Rows);
        Assert.Equal(selected.StartUs, row.ProcessStartUs);
        Assert.Equal(2_000, row.TotalHeapBytes);
        Assert.Equal(selected, response.SelectedProcess);
        Assert.Equal("single_process", response.ScopeMode);
        Assert.True(response.PidReuseObserved);
        Assert.Equal([selected], response.IncludedProcesses);
        Assert.Equal(1, response.MatchedEventCount);
        Assert.DoesNotContain(
            response.Warnings,
            warning => warning.Contains("identity_unresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeEvents_MissingExactSelectorReturnsStructuredEmptyResponse()
    {
        var response = GcHeapStatsAnalysis.AnalyzeEvents(
                traceEndUs: 200,
                processLifetimes:
                [
                    new ProcessLifetime(
                        new ProcessInstanceKey(10, 0), 200, true, false),
                ],
                events: [],
                pid: 10,
                window: new TimeWindow(0, 200),
                processStartUs: 100);

        Assert.Empty(response.Rows);
        Assert.Equal("scope_not_found", response.ScopeStatus);
        Assert.Equal("scope_not_found", response.NoDataReason);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal(0, response.MatchedEventCount);
    }

    private static GcHeapStatsEvent HeapStats(int pid, long timeUs, long heapBytes) =>
        new(
            Pid: pid,
            TimeUs: timeUs,
            TotalHeapBytes: heapBytes,
            Gen0Bytes: heapBytes / 10,
            Gen1Bytes: heapBytes / 10,
            Gen2Bytes: heapBytes / 2,
            LohBytes: heapBytes / 5,
            PohBytes: heapBytes / 10,
            PinnedObjectCount: 1,
            GcHandleCount: 2,
            FinalizationPromotedBytes: 3,
            FinalizationPromotedCount: 4);
}
