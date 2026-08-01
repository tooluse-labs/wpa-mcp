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
    public void RequireSingleProcess_ConvertsCleanAggregateToStructuredReplayRequest()
    {
        var aggregate = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 500), pid: 42, processStartUs: null, Lifetimes);

        var exactOnly = aggregate.RequireSingleProcess();

        Assert.False(exactOnly.IsResolved);
        Assert.Equal("unresolved", exactOnly.ScopeMode);
        Assert.Equal("process_start_required", exactOnly.ScopeStatus);
        Assert.True(exactOnly.PidReuseObserved);
        Assert.Null(exactOnly.SelectedProcess);
        Assert.Equal(aggregate.IncludedProcesses, exactOnly.IncludedProcesses);
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

    [Fact]
    public void DuplicateExactKey_IsCanonicalizedConsistentlyByScopeAndResolver()
    {
        var key = new ProcessInstanceKey(42, 0);
        ProcessLifetime[] duplicates =
        [
            new(key, 75, StartObserved: false, EndObserved: true),
            new(key, 100, StartObserved: false, EndObserved: false),
        ];

        var scope = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 100), pid: 42, processStartUs: 0, duplicates);
        var resolver = new ProcessInstanceResolver(duplicates);
        var resolution = resolver.Resolve(42, timestampUs: 50, processStartUs: 0);

        Assert.Equal("single_process", scope.ScopeMode);
        Assert.Equal([key], scope.IncludedProcesses);
        Assert.Equal(InstanceResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(key, resolution.Value);
        Assert.Equal(75, Assert.Single(resolver.Lifetimes).EndUs);

        var afterObservedStop = ProcessAnalysisScope.Resolve(
            new TimeWindow(80, 90), pid: 42, processStartUs: 0, duplicates);
        Assert.Equal(ProcessAnalysisScope.NotFoundStatus, afterObservedStop.ScopeStatus);
        Assert.Equal(
            InstanceResolutionStatus.Unresolved,
            resolver.Resolve(42, timestampUs: 80, processStartUs: 0).Status);
    }

    [Fact]
    public void ConflictingObservedStops_AreAuditableAndUseEarliestSafeBound()
    {
        var key = new ProcessInstanceKey(42, 0);
        ProcessLifetime[] conflicts =
        [
            new(key, 75, StartObserved: true, EndObserved: true),
            new(key, 90, StartObserved: true, EndObserved: true),
        ];

        var resolver = new ProcessInstanceResolver(conflicts);
        var beforeStop = resolver.Resolve(42, timestampUs: 50, processStartUs: 0);
        var beforeStopScope = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 70), pid: 42, processStartUs: 0, conflicts);
        var afterFirstStopScope = ProcessAnalysisScope.Resolve(
            new TimeWindow(80, 85), pid: 42, processStartUs: 0, conflicts);

        Assert.Equal(75, Assert.Single(resolver.Lifetimes).EndUs);
        Assert.Equal(InstanceResolutionStatus.Ambiguous, beforeStop.Status);
        Assert.Equal([key], beforeStop.Candidates);
        Assert.Equal("ambiguous_process_instance", beforeStopScope.ScopeStatus);
        Assert.Equal("unresolved", beforeStopScope.ScopeMode);
        Assert.Equal([key], beforeStopScope.IncludedProcesses);
        Assert.Equal(ProcessAnalysisScope.NotFoundStatus, afterFirstStopScope.ScopeStatus);
        Assert.Equal(
            InstanceResolutionStatus.Unresolved,
            resolver.Resolve(42, timestampUs: 80, processStartUs: 0).Status);
    }

    [Fact]
    public void Resolve_OverlappingReusedPidIsAmbiguousForAggregateAndExactSelector()
    {
        ProcessLifetime[] overlapping =
        [
            new(new ProcessInstanceKey(42, 0), 100, true, true),
            new(new ProcessInstanceKey(42, 50), 150, true, true),
        ];
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 150,
            overlapping,
            Array.Empty<ThreadLifecycleEvent>());

        var aggregate = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 150), pid: 42, processStartUs: null, identities);
        var exact = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 150), pid: 42, processStartUs: 0, identities);

        Assert.Equal("ambiguous_process_instance", aggregate.ScopeStatus);
        Assert.Equal("ambiguous_process_instance", exact.ScopeStatus);
        Assert.Equal("unresolved", aggregate.ScopeMode);
        Assert.Equal("unresolved", exact.ScopeMode);
        Assert.Equal(
            [new ProcessInstanceKey(42, 0), new ProcessInstanceKey(42, 50)],
            aggregate.IncludedProcesses);
        Assert.Equal(aggregate.IncludedProcesses, exact.IncludedProcesses);
        Assert.False(aggregate.TryResolveEventProcess(identities, 42, 75, out _));
        Assert.False(exact.TryResolveEventProcess(identities, 42, 75, out _));
    }
}
