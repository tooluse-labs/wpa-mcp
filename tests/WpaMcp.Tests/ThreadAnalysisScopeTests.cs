using WpaMcp.Analyzers;
using WpaMcp.Core;

namespace WpaMcp.Tests;

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
    public void Resolve_ThreadGenerationDisambiguatesEqualStartTimes()
    {
        var identities = EqualStartThreadIdentityIndex();
        var ambiguous = ThreadAnalysisScope.Resolve(
            new TimeWindow(0, 50),
            pid: 50,
            tid: 7,
            processStartUs: 0,
            threadStartUs: 0,
            identities);
        var exact = ThreadAnalysisScope.Resolve(
            new TimeWindow(0, 50),
            pid: 50,
            tid: 7,
            processStartUs: 0,
            threadStartUs: 0,
            identities,
            threadGeneration: 2);

        Assert.Equal(InstanceResolutionStatus.Ambiguous, ambiguous.Status);
        Assert.Equal([1L, 2L], ambiguous.Candidates
            .Select(candidate => candidate.Thread!.Key.Generation)
            .Order());
        Assert.Equal(InstanceResolutionStatus.Resolved, exact.Status);
        Assert.Equal(2, exact.Value!.Value.Thread!.Key.Generation);
        Assert.Equal(0, exact.Value.Value.Thread.StartUs);
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
        Assert.Equal(
            [new ProcessInstanceKey(50, 0), new ProcessInstanceKey(50, 200)],
            result.Value.Value.IncludedProcesses);
        Assert.Equal(2, result.Value.Value.IncludedProcessLifetimes?.Count);
        Assert.True(result.Value.Value.MatchesPoint(50, 7, 0));
        Assert.True(result.Value.Value.MatchesPoint(50, 7, 99));
        Assert.False(result.Value.Value.MatchesPoint(50, 7, 100));
        Assert.True(result.Value.Value.MatchesPoint(50, 7, 25));
        Assert.True(result.Value.Value.MatchesPoint(50, 9, 225));
        Assert.False(result.Value.Value.MatchesPoint(50, 8, 150));
        Assert.False(result.Value.Value.MatchesPoint(51, 9, 225));
        var first = new ThreadInstanceKey(
            new ProcessInstanceKey(50, 0), 7, 1);
        var second = new ThreadInstanceKey(
            new ProcessInstanceKey(50, 200), 9, 1);
        var excludedThird = new ThreadInstanceKey(
            new ProcessInstanceKey(50, 120), 8, 1);
        Assert.True(result.Value.Value.MatchesPoint(first, timestampUs: 0));
        Assert.False(result.Value.Value.MatchesPoint(first, timestampUs: 100));
        Assert.True(result.Value.Value.MatchesPoint(second, timestampUs: 200));
        Assert.False(result.Value.Value.MatchesPoint(
            excludedThird,
            timestampUs: 150));
        Assert.Equal(10, result.Value.Value.AccountInterval(first, 90, 110));
        Assert.Equal(10, result.Value.Value.AccountInterval(second, 190, 210));
        Assert.Equal(0, result.Value.Value.AccountInterval(excludedThird, 120, 180));
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
        Assert.True(result.Value.Value.PidReuseObserved);
        Assert.False(result.Value.Value.MatchesPoint(50, 7, 25));
        Assert.True(result.Value.Value.MatchesPoint(50, 9, 225));
    }

    [Fact]
    public void Resolve_PidOnlyWindowWithOneLifetimeSelectsItButReportsTraceWideReuse()
    {
        var result = ThreadAnalysisScope.Resolve(
            new TimeWindow(200, 300), 50, null, null, null,
            ReusedProcessIdentityIndex());

        Assert.Equal(InstanceResolutionStatus.Resolved, result.Status);
        var scope = result.Value!.Value;
        Assert.Equal(new ProcessInstanceKey(50, 200), scope.Process!.Key);
        Assert.False(scope.AggregatesPidLifetimes);
        Assert.True(scope.PidReuseObserved);
        Assert.False(scope.MatchesPoint(50, 7, 50));
        Assert.True(scope.MatchesPoint(50, 9, 225));
    }

    [Fact]
    public void Resolve_PidOnlyUniqueLifetimeIsSingleProcessNotAggregate()
    {
        var result = ThreadAnalysisScope.Resolve(
            new TimeWindow(0, 300), 50, null, null, null, IdentityIndex());

        Assert.Equal(InstanceResolutionStatus.Resolved, result.Status);
        var scope = result.Value!.Value;
        Assert.Equal(new ProcessInstanceKey(50, 20), scope.Process!.Key);
        Assert.False(scope.AggregatesPidLifetimes);
        Assert.False(scope.PidReuseObserved);
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
    public void Resolve_MissingPidOnly_IsUnresolved()
    {
        var result = ThreadAnalysisScope.Resolve(
            new TimeWindow(0, 300), 999, tid: null, processStartUs: null,
            threadStartUs: null, ReusedProcessIdentityIndex());

        Assert.Equal(InstanceResolutionStatus.Unresolved, result.Status);
        Assert.Null(result.Value);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void PidAggregateWithoutIncludedLifetimes_FailsClosed()
    {
        var thread = new ThreadInstanceKey(
            new ProcessInstanceKey(50, 0), Tid: 7, Generation: 1);
        var scope = new ThreadAnalysisScope(
            new TimeWindow(0, 100),
            Pid: 50,
            Process: null,
            Thread: null,
            AggregatesPidLifetimes: true,
            PidReuseObserved: false);

        Assert.False(scope.MatchesPoint(50, 7, timestampUs: 0));
        Assert.False(scope.MatchesPoint(thread, timestampUs: 0));
        Assert.Equal(0, scope.AccountInterval(thread, startUs: 0, endUs: 10));
    }

    [Fact]
    public void PidAggregate_RejectsReuseGapAndExcludedThirdLifetime()
    {
        var first = new ProcessLifetime(
            new ProcessInstanceKey(50, 0), EndUs: 40,
            StartObserved: true, EndObserved: true);
        var second = new ProcessLifetime(
            new ProcessInstanceKey(50, 60), EndUs: 100,
            StartObserved: true, EndObserved: true);
        var excludedThird = new ProcessLifetime(
            new ProcessInstanceKey(50, 120), EndUs: 160,
            StartObserved: true, EndObserved: true);
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 180,
            processes: [first, second, excludedThird],
            threads: []);
        var scope = new ThreadAnalysisScope(
            new TimeWindow(0, 180),
            Pid: 50,
            Process: null,
            Thread: null,
            AggregatesPidLifetimes: true,
            PidReuseObserved: true,
            IncludedProcesses: [first.Key, second.Key],
            IncludedProcessLifetimes: [first, second]);
        var excludedThread = new ThreadInstanceKey(
            excludedThird.Key, Tid: 7, Generation: 1);

        Assert.True(scope.MatchesPoint(50, tid: 7, timestampUs: 0));
        Assert.False(scope.MatchesPoint(50, tid: 7, timestampUs: 40));
        Assert.False(scope.MatchesPoint(50, tid: 7, timestampUs: 50));
        Assert.False(scope.MatchesPoint(50, tid: 7, timestampUs: 130));
        Assert.False(scope.MatchesRawUnresolvedCandidate(
            identities, 50, tid: 7, timestampUs: 130));
        Assert.False(scope.MatchesPoint(excludedThread, timestampUs: 130));
        Assert.Equal(0, scope.AccountInterval(excludedThread, 120, 140));
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

    private static TraceIdentityIndex EqualStartThreadIdentityIndex() =>
        TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 100,
            processes:
            [
                new ProcessLifetime(
                    new ProcessInstanceKey(50, 0), 100,
                    StartObserved: true, EndObserved: true),
            ],
            threads:
            [
                new ThreadLifecycleEvent(
                    50, 7, 20, ThreadLifecycleEventKind.Stop, Observed: true),
                new ThreadLifecycleEvent(
                    50, 7, 40, ThreadLifecycleEventKind.Stop, Observed: true),
            ]);
}
