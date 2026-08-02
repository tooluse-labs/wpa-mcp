using WpaMcp.Analyzers;
using WpaMcp.Core;
using Xunit;

namespace WpaMcp.Tests;

public class ThreadLifetimeAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ThreadLifetime_System_ReturnsCleanShape()
    {
        // PID 4 (System) is always running and has many kernel threads. The CPU profile
        // captures Thread events. Even if we don't get rich data, the shape must be valid.
        var cache = new TraceCache(capacity: 2);
        var trace = cache.Get(FixturePath);
        var resp = ThreadLifetimeAnalysis.Analyze(trace, pid: 4, top: 50);
        Assert.Equal(4, resp.Pid);
        // Total >= 0 (may be 0 if no Thread keyword captured for this process).
        Assert.True(resp.TotalThreads >= 0);
        // Top is bounded.
        Assert.True(resp.Threads.Count <= 50);
        Assert.True(resp.PeakConcurrentThreads >= 0);
        Assert.Equal("single_process", resp.ScopeMode);
        Assert.Equal(4, resp.SelectedProcess?.Pid);
        Assert.Equal([resp.SelectedProcess.GetValueOrDefault()], resp.IncludedProcesses);
    }

    [Fact]
    public void ThreadLifetime_NonexistentPid_EmptyAndWarns()
    {
        var cache = new TraceCache(capacity: 2);
        var trace = cache.Get(FixturePath);
        var resp = ThreadLifetimeAnalysis.Analyze(trace, pid: 999_999, top: 10);
        Assert.Equal(0, resp.TotalThreads);
        Assert.Empty(resp.Threads);
        Assert.NotEmpty(resp.Warnings);
        Assert.Null(resp.SelectedProcess);
        Assert.Equal("unresolved", resp.ScopeMode);
        Assert.Equal("scope_not_found", resp.ScopeStatus);
        Assert.Equal("scope_not_found", resp.NoDataReason);
        Assert.Equal("unknown", resp.CapabilityStatus);
        Assert.Equal(0, resp.MatchedEventCount);
        Assert.False(resp.PidReuseObserved);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ProcessInstanceKey>>(
            resp.IncludedProcesses));
    }

    [Fact]
    public void ThreadLifetime_LifetimeUsConsistentWithEndMinusStart()
    {
        var cache = new TraceCache(capacity: 2);
        var trace = cache.Get(FixturePath);
        var resp = ThreadLifetimeAnalysis.Analyze(trace, pid: 4, top: 200);
        foreach (var t in resp.Threads)
            Assert.Equal(t.EndTimeUs - t.StartTimeUs, t.LifetimeUs);
    }

    [Fact]
    public void AnalyzeEvents_ReusedPidAndTidReturnsOnlySelectedProcessInstance()
    {
        var selected = new ProcessInstanceKey(20, 100);
        var rows = ThreadLifetimeAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes:
            [
                new ProcessLifetime(new ProcessInstanceKey(20, 0), 100, true, true),
                new ProcessLifetime(selected, 200, true, false),
            ],
            events:
            [
                new ThreadLifecycleEvent(20, 7, 10, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(20, 7, 90, ThreadLifecycleEventKind.Stop, Observed: true),
                new ThreadLifecycleEvent(20, 7, 110, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(20, 7, 190, ThreadLifecycleEventKind.Stop, Observed: true),
            ],
            selector: selected);

        var row = Assert.Single(rows);
        Assert.Equal(7, row.Tid);
        Assert.Equal(selected.StartUs, row.ProcessStartUs);
        Assert.Equal(1, row.ThreadGeneration);
        Assert.Equal(110, row.StartTimeUs);
        Assert.Equal(190, row.EndTimeUs);
        Assert.All(rows, candidate => Assert.True(candidate.StartTimeUs >= selected.StartUs));
    }

    [Fact]
    public void AnalyzeEvents_TraceResidentStartUsesProvenanceRatherThanZeroTimestamp()
    {
        var process = new ProcessInstanceKey(20, 0);
        var rows = ThreadLifetimeAnalysis.AnalyzeEvents(
            traceEndUs: 100,
            processLifetimes: [new ProcessLifetime(process, 100, true, false)],
            events:
            [
                new ThreadLifecycleEvent(20, 7, 0, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(20, 8, 0, ThreadLifecycleEventKind.RundownStart, Observed: false),
                new ThreadLifecycleEvent(20, 7, 20, ThreadLifecycleEventKind.Stop, Observed: true),
                new ThreadLifecycleEvent(20, 8, 20, ThreadLifecycleEventKind.Stop, Observed: true),
            ],
            selector: process);

        Assert.False(Assert.Single(rows, row => row.Tid == 7).TraceResidentStart);
        Assert.True(Assert.Single(rows, row => row.Tid == 8).TraceResidentStart);
    }

    [Fact]
    public void AnalyzeEventsResponse_ReusedPidWithoutSelectorReturnsStructuredFailure()
    {
        var response = ThreadLifetimeAnalysis.AnalyzeEventsResponse(
            traceEndUs: 400,
            processLifetimes:
            [
                new ProcessLifetime(new ProcessInstanceKey(20, 100), 200, true, true),
                new ProcessLifetime(new ProcessInstanceKey(20, 300), 400, true, false),
            ],
            events: [],
            pid: 20,
            top: 20,
            processStartUs: null);

        Assert.Empty(response.Threads);
        Assert.Equal("unresolved", response.ScopeMode);
        Assert.Equal("process_start_required", response.ScopeStatus);
        Assert.Equal("process_start_required", response.NoDataReason);
        Assert.Equal(
            [new ProcessInstanceKey(20, 100), new ProcessInstanceKey(20, 300)],
            response.IncludedProcesses);
        Assert.Contains(response.Warnings, warning =>
            warning.StartsWith("process_start_required:", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeEventsResponse_ExactSelectorFiltersProcessAndPreservesThreadGeneration()
    {
        var selected = new ProcessInstanceKey(20, 300);
        var response = ThreadLifetimeAnalysis.AnalyzeEventsResponse(
            traceEndUs: 500,
            processLifetimes:
            [
                new ProcessLifetime(new ProcessInstanceKey(20, 100), 250, true, true),
                new ProcessLifetime(selected, 500, true, false),
            ],
            events:
            [
                new ThreadLifecycleEvent(20, 7, 110, ThreadLifecycleEventKind.Start, true),
                new ThreadLifecycleEvent(20, 7, 200, ThreadLifecycleEventKind.Stop, true),
                new ThreadLifecycleEvent(20, 7, 310, ThreadLifecycleEventKind.Start, true),
                new ThreadLifecycleEvent(20, 7, 350, ThreadLifecycleEventKind.Stop, true),
                new ThreadLifecycleEvent(20, 7, 370, ThreadLifecycleEventKind.Start, true),
                new ThreadLifecycleEvent(20, 7, 450, ThreadLifecycleEventKind.Stop, true),
            ],
            pid: 20,
            top: 20,
            processStartUs: selected.StartUs,
            processName: "selected");

        Assert.Equal(selected, response.SelectedProcess);
        Assert.Equal("single_process", response.ScopeMode);
        Assert.True(response.PidReuseObserved);
        Assert.Equal([selected], response.IncludedProcesses);
        Assert.Equal("selected", response.ProcessName);
        Assert.Equal(2, response.TotalThreads);
        Assert.Equal(4, response.MatchedEventCount);
        Assert.Equal("observed", response.CapabilityStatus);
        Assert.Null(response.NoDataReason);
        Assert.Equal(1, response.PeakConcurrentThreads);
        Assert.All(response.Threads, row => Assert.Equal(selected.StartUs, row.ProcessStartUs));
        Assert.Equal([1L, 2L], response.Threads.Select(row => row.ThreadGeneration));
        Assert.Equal([310L, 370L], response.Threads.Select(row => row.StartTimeUs));
    }

    [Fact]
    public void AnalyzeEventsResponse_MissingExactSelectorReturnsStructuredEmptyResponse()
    {
        var response = ThreadLifetimeAnalysis.AnalyzeEventsResponse(
                traceEndUs: 200,
                processLifetimes:
                [
                    new ProcessLifetime(
                        new ProcessInstanceKey(20, 100), 200, true, false),
                ],
                events: [],
                pid: 20,
                top: 20,
                processStartUs: 101);

        Assert.Empty(response.Threads);
        Assert.Equal("scope_not_found", response.ScopeStatus);
        Assert.Equal("scope_not_found", response.NoDataReason);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal(0, response.MatchedEventCount);
    }

    [Fact]
    public void AnalyzeEvents_ExposesObservedReplacementProcessAndTraceEndBoundaries()
    {
        var process = new ProcessInstanceKey(20, 0);
        var processEnded = ThreadLifetimeAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes: [new ProcessLifetime(process, 100, true, true)],
            events:
            [
                new ThreadLifecycleEvent(20, 7, 10, ThreadLifecycleEventKind.Start, true),
                new ThreadLifecycleEvent(20, 7, 20, ThreadLifecycleEventKind.Start, true),
                new ThreadLifecycleEvent(20, 7, 30, ThreadLifecycleEventKind.Stop, true),
                new ThreadLifecycleEvent(20, 8, 0, ThreadLifecycleEventKind.RundownStart, false),
            ],
            selector: process);

        var replaced = Assert.Single(processEnded, row =>
            row.Tid == 7 && row.ThreadGeneration == 1);
        Assert.Equal("observed", replaced.StartBoundaryKind);
        Assert.Equal("replacement", replaced.EndBoundaryKind);
        Assert.Equal("bounded_by_inferred_endpoint", replaced.MeasurementState);
        var observed = Assert.Single(processEnded, row =>
            row.Tid == 7 && row.ThreadGeneration == 2);
        Assert.Equal("observed", observed.EndBoundaryKind);
        Assert.Equal("exact_observed_interval", observed.MeasurementState);
        var processBound = Assert.Single(processEnded, row => row.Tid == 8);
        Assert.Equal("process_start", processBound.StartBoundaryKind);
        Assert.Equal("process_end", processBound.EndBoundaryKind);

        var traceBound = ThreadLifetimeAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes: [new ProcessLifetime(process, 200, true, false)],
            events:
            [
                new ThreadLifecycleEvent(20, 9, 40, ThreadLifecycleEventKind.Start, true),
            ],
            selector: process);
        Assert.Equal("trace_end", Assert.Single(traceBound).EndBoundaryKind);
    }
}
