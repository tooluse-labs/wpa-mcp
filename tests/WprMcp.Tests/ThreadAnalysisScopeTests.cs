using WprMcp.Analyzers;
using WprMcp.Core;

namespace WprMcp.Tests;

public sealed class ThreadAnalysisScopeTests
{
    [Fact]
    public void Resolve_UniqueThread_ProducesLifetimeBoundScope()
    {
        var result = ThreadAnalysisScope.Resolve(
            window: new TimeWindow(100, 200),
            pid: 50,
            tid: 7,
            processStartUs: 20,
            threadStartUs: 80,
            identities: IdentityIndex());

        Assert.Equal(InstanceResolutionStatus.Resolved, result.Status);
        Assert.True(result.Value.HasValue);
        var scope = result.Value.Value;
        Assert.True(scope.MatchesPoint(pid: 50, tid: 7, timestampUs: 150));
        Assert.False(scope.MatchesPoint(pid: 50, tid: 8, timestampUs: 150));
        Assert.False(scope.MatchesPoint(pid: 50, tid: 7, timestampUs: 200));
        Assert.Equal(
            100,
            scope.AccountInterval(scope.Thread!.Key, startUs: 50, endUs: 250));
    }

    [Fact]
    public void Resolve_ReusedTidAcrossWindow_IsAmbiguous()
    {
        var result = ThreadAnalysisScope.Resolve(
            new TimeWindow(0, 300), 50, 7, null, null, ReusedThreadIdentityIndex());

        Assert.Equal(InstanceResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Resolve_ThreadStartSelector_DisambiguatesGeneration()
    {
        var result = ThreadAnalysisScope.Resolve(
            new TimeWindow(0, 300), 50, 7, processStartUs: 20,
            threadStartUs: 100, ReusedThreadIdentityIndex());

        Assert.Equal(InstanceResolutionStatus.Resolved, result.Status);
        Assert.Equal(100, result.Value!.Value.Thread!.StartUs);
    }

    [Fact]
    public void Resolve_LegacyPidOnly_AggregatesReusedProcessInstancesAndWarns()
    {
        var result = ThreadAnalysisScope.Resolve(
            new TimeWindow(0, 300), 50, null, null, null,
            ReusedProcessIdentityIndex());

        Assert.Equal(InstanceResolutionStatus.Resolved, result.Status);
        Assert.Null(result.Value!.Value.Process);
        Assert.True(result.Value.Value.AggregatesPidLifetimes);
        Assert.True(result.Value.Value.PidReuseObserved);
        Assert.True(result.Value.Value.MatchesPoint(50, 7, 25));
        Assert.True(result.Value.Value.MatchesPoint(50, 9, 225));
        Assert.False(result.Value.Value.MatchesPoint(51, 9, 225));
    }

    [Fact]
    public void Resolve_ExactProcessStart_BindsOneProcessLifetime()
    {
        var result = ThreadAnalysisScope.Resolve(
            new TimeWindow(0, 300), 50, null, processStartUs: 200,
            threadStartUs: null, ReusedProcessIdentityIndex());

        Assert.Equal(InstanceResolutionStatus.Resolved, result.Status);
        Assert.Equal(new ProcessInstanceKey(50, 200), result.Value!.Value.Process!.Key);
        Assert.False(result.Value.Value.AggregatesPidLifetimes);
        Assert.False(result.Value.Value.PidReuseObserved);
        Assert.False(result.Value.Value.MatchesPoint(50, 7, 25));
        Assert.True(result.Value.Value.MatchesPoint(50, 9, 225));
    }

    [Fact]
    public void Resolve_MissingExactProcessOrThread_IsUnresolved()
    {
        var identities = ReusedProcessIdentityIndex();

        var process = ThreadAnalysisScope.Resolve(
            new TimeWindow(0, 300), 50, null, processStartUs: 123,
            threadStartUs: null, identities);
        var thread = ThreadAnalysisScope.Resolve(
            new TimeWindow(0, 300), 50, 999, processStartUs: null,
            threadStartUs: null, identities);

        Assert.Equal(InstanceResolutionStatus.Unresolved, process.Status);
        Assert.Equal(InstanceResolutionStatus.Unresolved, thread.Status);
        Assert.Empty(process.Candidates);
        Assert.Empty(thread.Candidates);
    }

    [Fact]
    public void Resolve_NoPid_ProducesAllProcessWindowScope()
    {
        var result = ThreadAnalysisScope.Resolve(
            new TimeWindow(10, 20), null, null, null, null, IdentityIndex());

        Assert.Equal(InstanceResolutionStatus.Resolved, result.Status);
        Assert.Null(result.Value!.Value.Pid);
        Assert.True(result.Value.Value.MatchesPoint(999, 888, 15));
        Assert.False(result.Value.Value.MatchesPoint(999, 888, 20));
    }

    [Fact]
    public void MatchesPoint_ResolvedIdentityRejectsPriorInstanceAtReuseBoundary()
    {
        var selectedProcess = new ProcessLifetime(
            new ProcessInstanceKey(50, 100),
            EndUs: 200,
            StartObserved: true,
            EndObserved: true);
        var selectedThread = new ThreadLifetime(
            new ThreadInstanceKey(selectedProcess.Key, Tid: 7, Generation: 1),
            StartUs: 100,
            EndUs: 180,
            StartObserved: true,
            EndObserved: true);
        var priorThread = new ThreadInstanceKey(
            new ProcessInstanceKey(50, 0),
            Tid: 7,
            Generation: 1);
        var scope = new ThreadAnalysisScope(
            new TimeWindow(0, 200),
            Pid: 50,
            Process: selectedProcess,
            Thread: selectedThread,
            AggregatesPidLifetimes: false,
            PidReuseObserved: false);

        Assert.False(scope.MatchesPoint(priorThread, timestampUs: 100));
        Assert.True(scope.MatchesPoint(selectedThread.Key, timestampUs: 100));
    }

    private static TraceIdentityIndex IdentityIndex() =>
        TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 300,
            processes:
            [
                new ProcessLifetime(
                    new ProcessInstanceKey(50, 20), 250,
                    StartObserved: true, EndObserved: true),
            ],
            threads:
            [
                new ThreadLifecycleEvent(50, 7, 80, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(50, 7, 220, ThreadLifecycleEventKind.Stop, Observed: true),
            ]);

    private static TraceIdentityIndex ReusedThreadIdentityIndex() =>
        TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 300,
            processes:
            [
                new ProcessLifetime(
                    new ProcessInstanceKey(50, 20), 280,
                    StartObserved: true, EndObserved: true),
            ],
            threads:
            [
                new ThreadLifecycleEvent(50, 7, 40, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(50, 7, 80, ThreadLifecycleEventKind.Stop, Observed: true),
                new ThreadLifecycleEvent(50, 7, 100, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(50, 7, 260, ThreadLifecycleEventKind.Stop, Observed: true),
            ]);

    private static TraceIdentityIndex ReusedProcessIdentityIndex() =>
        TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 300,
            processes:
            [
                new ProcessLifetime(
                    new ProcessInstanceKey(50, 0), 100,
                    StartObserved: true, EndObserved: true),
                new ProcessLifetime(
                    new ProcessInstanceKey(50, 200), 300,
                    StartObserved: true, EndObserved: false),
            ],
            threads:
            [
                new ThreadLifecycleEvent(50, 7, 10, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(50, 7, 90, ThreadLifecycleEventKind.Stop, Observed: true),
                new ThreadLifecycleEvent(50, 9, 210, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(50, 9, 290, ThreadLifecycleEventKind.Stop, Observed: true),
            ]);
}
