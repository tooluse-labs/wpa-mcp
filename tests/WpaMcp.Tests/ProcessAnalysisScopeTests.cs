using WpaMcp.Analyzers;
using WpaMcp.Core;

namespace WpaMcp.Tests;

public sealed class ProcessAnalysisScopeTests
{
    private static readonly ProcessLifetime[] Lifetimes =
    [
        new(new ProcessInstanceKey(42, 100), 200, true, true),
        new(new ProcessInstanceKey(42, 300), 400, true, true),
        new(new ProcessInstanceKey(50, 50), 450, true, true),
    ];

    [Fact]
    public void Resolve_PidReuseInsideWindow_IsExplicitAggregate()
    {
        var scope = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 500), pid: 42, processStartUs: null, Lifetimes);

        Assert.True(scope.IsResolved);
        Assert.Equal("ok", scope.ScopeStatus);
        Assert.Equal("pid_aggregate", scope.ScopeMode);
        Assert.True(scope.PidReuseObserved);
        Assert.Null(scope.SelectedProcess);
        Assert.Equal(
            [new ProcessInstanceKey(42, 100), new ProcessInstanceKey(42, 300)],
            scope.IncludedProcesses);
    }

    [Fact]
    public void Resolve_ExactStart_SelectsOneInstance()
    {
        var scope = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 500), pid: 42, processStartUs: 300, Lifetimes);

        Assert.True(scope.IsResolved);
        Assert.Equal("single_process", scope.ScopeMode);
        Assert.Equal(new ProcessInstanceKey(42, 300), scope.SelectedProcess);
        Assert.Equal([new ProcessInstanceKey(42, 300)], scope.IncludedProcesses);
        Assert.True(scope.PidReuseObserved);
    }

    [Fact]
    public void Resolve_MissingPidOrExactInstance_ReturnsStructuredNotFound()
    {
        var missingPid = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 500), pid: 99, processStartUs: null, Lifetimes);
        var missingInstance = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 500), pid: 42, processStartUs: 999, Lifetimes);

        Assert.False(missingPid.IsResolved);
        Assert.Equal("scope_not_found", missingPid.ScopeStatus);
        Assert.Equal("unresolved", missingPid.ScopeMode);
        Assert.Empty(missingPid.IncludedProcesses);
        Assert.False(missingInstance.IsResolved);
        Assert.Equal("scope_not_found", missingInstance.ScopeStatus);
        Assert.Equal("unresolved", missingInstance.ScopeMode);
        Assert.Empty(missingInstance.IncludedProcesses);
    }

    [Fact]
    public void Resolve_WindowRestrictsIncludedLifetimes_ButReportsTraceWidePidReuse()
    {
        var scope = ProcessAnalysisScope.Resolve(
            new TimeWindow(250, 450), pid: 42, processStartUs: null, Lifetimes);

        Assert.Equal("single_process", scope.ScopeMode);
        Assert.Equal(new ProcessInstanceKey(42, 300), scope.SelectedProcess);
        Assert.Equal([new ProcessInstanceKey(42, 300)], scope.IncludedProcesses);
        Assert.True(scope.PidReuseObserved);
    }

    [Fact]
    public void TryResolveEventProcess_RejectsOtherLifetimeAndWindow()
    {
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 500,
            Lifetimes,
            Array.Empty<ThreadLifecycleEvent>());
        var scope = ProcessAnalysisScope.Resolve(
            new TimeWindow(250, 450), pid: 42, processStartUs: 300, identities);

        Assert.False(scope.TryResolveEventProcess(identities, 42, 150, out _));
        Assert.True(scope.TryResolveEventProcess(identities, 42, 350, out var process));
        Assert.Equal(new ProcessInstanceKey(42, 300), process);
        Assert.False(scope.TryResolveEventProcess(identities, 50, 350, out _));
        Assert.False(scope.TryResolveEventProcess(identities, 42, 450, out _));
    }

    [Fact]
    public void MatchesEvent_AllProcessesPreservesUnresolvedIdentityWithinWindow()
    {
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 500,
            Lifetimes,
            Array.Empty<ThreadLifecycleEvent>());
        var scope = ProcessAnalysisScope.Resolve(
            new TimeWindow(100, 400), pid: null, processStartUs: null, identities);

        Assert.True(scope.MatchesEvent(identities, eventPid: 999, timestampUs: 200));
        Assert.False(scope.TryResolveEventProcess(
            identities, eventPid: 999, timestampUs: 200, out _));
        Assert.False(scope.MatchesEvent(identities, eventPid: 999, timestampUs: 400));
    }
}
