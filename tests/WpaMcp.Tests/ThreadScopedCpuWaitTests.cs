using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public sealed class ThreadScopedCpuWaitTests
{
    private const string CpuFixturePath = "fixtures/small_cpu.etl";
    private const string WaitFixturePath = "fixtures/small_wait_bound.etl";

    [Fact]
    public void SixThreadTools_RejectIncompleteThreadSelectorsBeforeTraceAccess()
    {
        var cache = new TraceCache(capacity: 1);
        var wait = new WaitTools(cache);
        var cpu = new CpuTools(cache);

        Assert.Throws<ArgumentException>(() => wait.WaitAnalysis("missing.etl", tid: 7));
        Assert.Throws<ArgumentException>(() => wait.WaitTopStacks("missing.etl", tid: 7));
        Assert.Throws<ArgumentException>(() => wait.WaitCallerCallee("missing.etl", "x", tid: 7));
        Assert.Throws<ArgumentException>(() => cpu.CpuPreciseAnalysis("missing.etl", tid: 7));
        Assert.Throws<ArgumentException>(() => cpu.CpuTopFunctions("missing.etl", tid: 7));
        Assert.Throws<ArgumentException>(() => cpu.CpuCallerCallee("missing.etl", "x", tid: 7));
        Assert.Throws<ArgumentException>(() =>
            wait.WaitAnalysis("missing.etl", pid: 1, threadGeneration: 2));
        Assert.Throws<ArgumentException>(() =>
            wait.WaitTopStacks("missing.etl", pid: 1, threadGeneration: 2));
        Assert.Throws<ArgumentException>(() =>
            wait.WaitCallerCallee("missing.etl", "x", pid: 1, threadGeneration: 2));
        Assert.Throws<ArgumentException>(() =>
            cpu.CpuPreciseAnalysis("missing.etl", pid: 1, threadGeneration: 2));
        Assert.Throws<ArgumentException>(() =>
            cpu.CpuTopFunctions("missing.etl", pid: 1, threadGeneration: 2));
        Assert.Throws<ArgumentException>(() =>
            cpu.CpuCallerCallee("missing.etl", "x", pid: 1, threadGeneration: 2));
    }

    [Fact]
    public void ExactWaitThread_SummaryStacksCallerCalleeAndBucketsShareTotal()
    {
        WithSymbolPathUnset(() =>
        {
            var cache = new TraceCache(capacity: 2);
            var lifetime = FindBlockedThread(cache);
            var wait = new WaitTools(cache);
            var cpu = new CpuTools(cache);

            var summary = wait.WaitAnalysis(
                WaitFixturePath,
                top: 1,
                pid: lifetime.Key.Process.Pid,
                tid: lifetime.Key.Tid,
                processStartUs: lifetime.Key.Process.StartUs,
                threadStartUs: lifetime.StartUs,
                threadGeneration: lifetime.Key.Generation);
            var stacks = wait.WaitTopStacks(
                WaitFixturePath,
                top: 10,
                pid: lifetime.Key.Process.Pid,
                whenBuckets: 17,
                resolveSymbols: false,
                tid: lifetime.Key.Tid,
                processStartUs: lifetime.Key.Process.StartUs,
                threadStartUs: lifetime.StartUs,
                threadGeneration: lifetime.Key.Generation);

            Assert.Equal(lifetime.Key, summary.SelectedThread);
            Assert.Equal("ok", summary.ScopeStatus);
            Assert.Equal("single_process", summary.ScopeMode);
            Assert.Equal([lifetime.Key.Process], summary.IncludedProcesses);
            Assert.Equal([Candidate(lifetime)], summary.IncludedThreads);
            Assert.Equal(summary.ScopedCSwitches, summary.MatchedEventCount);
            var summaryRow = Assert.Single(summary.Rows);
            Assert.Equal(lifetime.StartUs, summaryRow.ThreadStartUs);
            Assert.True(summary.HasContextSwitches);
            Assert.True(summary.HasContextSwitchBlockingStacks);
            Assert.Equal(lifetime.Key, stacks.SelectedThread);
            Assert.Equal([Candidate(lifetime)], stacks.IncludedThreads);
            Assert.Equal(summary.TotalBlockedUs, stacks.TotalBlockedUs);
            Assert.Equal(stacks.TotalBlockedUs, stacks.When!.Buckets.Sum());
            Assert.True(stacks.HasContextSwitches);
            Assert.True(stacks.HasContextSwitchBlockingStacks);
            Assert.Equal(stacks.UnmatchedBlockedIntervalCount,
                stacks.TraceUnmatchedBlockedIntervalCount);
            Assert.True(stacks.ScopedUnmatchedBlockedIntervalCount <=
                        stacks.TraceUnmatchedBlockedIntervalCount);
            Assert.Equal(stacks.ScopedCSwitches > 0, stacks.HasContextSwitches);
            Assert.Equal(stacks.ScopedStackedSwitches > 0,
                stacks.HasContextSwitchBlockingStacks);
            Assert.Equal("skipped", stacks.SymbolResolutionState);

            var focus = Assert.Single(stacks.Rows.Take(1)).Function;
            var callerCallee = wait.WaitCallerCallee(
                WaitFixturePath,
                focus,
                top: 10,
                pid: lifetime.Key.Process.Pid,
                resolveSymbols: false,
                tid: lifetime.Key.Tid,
                processStartUs: lifetime.Key.Process.StartUs,
                threadStartUs: lifetime.StartUs,
                threadGeneration: lifetime.Key.Generation);

            Assert.Equal(lifetime.Key, callerCallee.SelectedThread);
            Assert.Equal([Candidate(lifetime)], callerCallee.IncludedThreads);
            Assert.Equal(summary.TotalBlockedUs, callerCallee.SourceTotalMetric);
            Assert.Equal(stacks.UnmatchedBlockedIntervalCount, callerCallee.UnmatchedIntervalCount);
            Assert.Equal(stacks.TraceUnmatchedBlockedIntervalCount,
                callerCallee.TraceUnmatchedIntervalCount);
            Assert.Equal(stacks.ScopedUnmatchedBlockedIntervalCount,
                callerCallee.ScopedUnmatchedIntervalCount);
            Assert.Equal(stacks.ScopedCSwitches, callerCallee.ScopedCSwitches);
            Assert.True(callerCallee.HasContextSwitchBlockingStacks);

            var precise = cpu.CpuPreciseAnalysis(
                WaitFixturePath,
                top: 1,
                pid: lifetime.Key.Process.Pid,
                tid: lifetime.Key.Tid,
                processStartUs: lifetime.Key.Process.StartUs,
                threadStartUs: lifetime.StartUs,
                threadGeneration: lifetime.Key.Generation);
            Assert.Equal(lifetime.Key, precise.SelectedThread);
            Assert.Equal([Candidate(lifetime)], precise.IncludedThreads);
            var preciseRow = Assert.Single(precise.Rows);
            Assert.Equal(lifetime.Key.Tid, preciseRow.Tid);
            Assert.Equal(lifetime.StartUs, preciseRow.ThreadStartUs);
            Assert.Equal(summaryRow.CpuUs, precise.TotalCpuUs);

            var symbolized = wait.WaitTopStacks(
                WaitFixturePath,
                top: 10,
                pid: lifetime.Key.Process.Pid,
                resolveSymbols: true,
                tid: lifetime.Key.Tid,
                processStartUs: lifetime.Key.Process.StartUs,
                threadStartUs: lifetime.StartUs,
                threadGeneration: lifetime.Key.Generation);
            Assert.Equal(stacks.SelectedThread, symbolized.SelectedThread);
            Assert.Equal(stacks.TotalBlockedUs, symbolized.TotalBlockedUs);
            Assert.Equal(stacks.SampleCount, symbolized.SampleCount);
        });
    }

    [Fact]
    public void WaitExactSelectorFailure_ReturnsStructuredScopeNotFound()
    {
        var wait = new WaitTools(new TraceCache(capacity: 1));
        var missingThread = wait.WaitAnalysis(
            WaitFixturePath,
            pid: int.MaxValue,
            tid: int.MaxValue);
        Assert.Equal("scope_not_found", missingThread.ScopeStatus);
        Assert.Equal("scope_not_found", missingThread.NoDataReason);
        Assert.Empty(missingThread.Rows);

        var missingProcess = wait.WaitAnalysis(
            WaitFixturePath,
            pid: int.MaxValue,
            processStartUs: long.MaxValue);
        Assert.Equal("scope_not_found", missingProcess.ScopeStatus);
        Assert.Equal("scope_not_found", missingProcess.NoDataReason);
        Assert.Empty(missingProcess.Rows);
    }

    [Fact]
    public void StackTools_MissingPid_ReturnStructuredProcessScopeFailure()
    {
        var cache = new TraceCache(capacity: 2);
        var cpu = new CpuTools(cache);
        var wait = new WaitTools(cache);

        AssertMissingProcess(cpu.CpuTopFunctions(CpuFixturePath, pid: int.MaxValue));
        AssertMissingProcess(cpu.CpuPreciseAnalysis(CpuFixturePath, pid: int.MaxValue));
        AssertMissingProcess(cpu.CpuCallerCallee(
            CpuFixturePath, "missing-focus", pid: int.MaxValue));
        AssertMissingProcess(wait.WaitAnalysis(WaitFixturePath, pid: int.MaxValue));
        AssertMissingProcess(wait.WaitTopStacks(
            WaitFixturePath, pid: int.MaxValue));
        AssertMissingProcess(wait.WaitCallerCallee(
            WaitFixturePath, "missing-focus", pid: int.MaxValue));
    }

    [Fact]
    public void StackTools_ExistingPidMissingTid_ReturnStructuredThreadScopeFailure()
    {
        var cache = new TraceCache(capacity: 2);
        var cpuProcess = FindExistingProcess(cache, CpuFixturePath);
        var waitProcess = FindExistingProcess(cache, WaitFixturePath);
        var cpu = new CpuTools(cache);
        var wait = new WaitTools(cache);

        AssertMissingThread(cpu.CpuTopFunctions(
            CpuFixturePath,
            pid: cpuProcess.Pid,
            tid: int.MaxValue,
            processStartUs: cpuProcess.StartUs), cpuProcess);
        AssertMissingThread(cpu.CpuPreciseAnalysis(
            CpuFixturePath,
            pid: cpuProcess.Pid,
            tid: int.MaxValue,
            processStartUs: cpuProcess.StartUs), cpuProcess);
        AssertMissingThread(cpu.CpuCallerCallee(
            CpuFixturePath,
            "missing-focus",
            pid: cpuProcess.Pid,
            tid: int.MaxValue,
            processStartUs: cpuProcess.StartUs), cpuProcess);
        AssertMissingThread(wait.WaitTopStacks(
            WaitFixturePath,
            pid: waitProcess.Pid,
            tid: int.MaxValue,
            processStartUs: waitProcess.StartUs), waitProcess);
        AssertMissingThread(wait.WaitAnalysis(
            WaitFixturePath,
            pid: waitProcess.Pid,
            tid: int.MaxValue,
            processStartUs: waitProcess.StartUs), waitProcess);
        AssertMissingThread(wait.WaitCallerCallee(
            WaitFixturePath,
            "missing-focus",
            pid: waitProcess.Pid,
            tid: int.MaxValue,
            processStartUs: waitProcess.StartUs), waitProcess);
    }

    [Fact]
    public void StackTools_AmbiguousTid_ReturnCandidatesWithoutPidFallback()
    {
        var identities = ReusedStackIdentityIndex();
        var window = new TimeWindow(0, 400);
        var candidates = identities.Threads.Lifetimes
            .Where(lifetime => lifetime.Key.Process.Pid == 50 && lifetime.Key.Tid == 7)
            .Select(Candidate)
            .ToArray();

        var cpuScope = CpuTools.ResolveStackScope(
            window, 50, 7, null, null, identities);
        var waitScope = WaitTools.ResolveStackScope(
            window, 50, 7, null, null, identities);
        AssertAmbiguousThread(cpuScope, candidates);
        AssertAmbiguousThread(waitScope, candidates);
        AssertAmbiguousThread(CpuPreciseAnalysis.EmptyScope(cpuScope), candidates);
        AssertAmbiguousThread(WaitAnalysis.EmptyResolutionFailure(waitScope), candidates);
    }

    [Fact]
    public void StackTools_PidAggregate_ListsEveryIncludedProcessLifetime()
    {
        var identities = ReusedStackIdentityIndex();
        var window = new TimeWindow(0, 400);
        var processes = identities.Processes.Lifetimes
            .Where(lifetime => lifetime.Key.Pid == 50)
            .Select(lifetime => lifetime.Key)
            .ToArray();

        AssertPidAggregate(
            CpuTools.ResolveStackScope(
                window, 50, null, null, null, identities),
            processes);
        AssertPidAggregate(
            WaitTools.ResolveStackScope(
                window, 50, null, null, null, identities),
            processes);
    }

    [Fact]
    public void ExactNewWaitInstanceDoesNotCountSwitchInAtReuseBoundary()
    {
        var oldThread = new ThreadInstanceKey(
            new ProcessInstanceKey(Pid: 100, StartUs: 0),
            Tid: 42,
            Generation: 1);
        var selectedProcess = new ProcessLifetime(
            new ProcessInstanceKey(Pid: 100, StartUs: 100),
            EndUs: 200,
            StartObserved: true,
            EndObserved: true);
        var selectedThread = new ThreadLifetime(
            new ThreadInstanceKey(selectedProcess.Key, Tid: 42, Generation: 1),
            StartUs: 100,
            EndUs: 180,
            StartObserved: true,
            EndObserved: true);
        var scope = new ThreadAnalysisScope(
            new TimeWindow(0, 200),
            Pid: 100,
            Process: selectedProcess,
            Thread: selectedThread,
            AggregatesPidLifetimes: false,
            PidReuseObserved: false);
        var projection = new WaitAnalysis.WaitProjectionAccumulator(scope);
        var observation = new SchedulerSwitchObservation(
            OldThread: oldThread,
            OldProcessName: "old-target",
            NewThread: selectedThread.Key,
            NewProcessName: "new-target",
            TimestampUs: 100,
            BlockingStack: Microsoft.Diagnostics.Tracing.Etlx.CallStackIndex.Invalid);

        projection.OnContextSwitch(observation);
        var response = projection.Build(
            top: 1,
            unmatchedBlockedIntervalCount: 0,
            warnings: null);

        var row = Assert.Single(response.Rows);
        Assert.Equal(0, row.ContextSwitches);
    }

    [Fact]
    public void ExactCpuThread_TopAndCallerCalleeShareSelectedSamples()
    {
        WithSymbolPathUnset(() =>
        {
            var cache = new TraceCache(capacity: 2);
            var lifetime = FindSampledThread(cache);
            var cpu = new CpuTools(cache);

            var top = cpu.CpuTopFunctions(
                CpuFixturePath,
                top: 1000,
                pid: lifetime.Key.Process.Pid,
                resolveSymbols: false,
                tid: lifetime.Key.Tid,
                processStartUs: lifetime.Key.Process.StartUs,
                threadStartUs: lifetime.StartUs,
                threadGeneration: lifetime.Key.Generation);

            Assert.Equal(lifetime.Key, top.SelectedThread);
            Assert.Equal([Candidate(lifetime)], top.IncludedThreads);
            Assert.True(top.TotalSamples > 0);
            Assert.Equal(
                top.HasSampledProfileStacks ? "skipped" : "no_stacks",
                top.SymbolResolutionState);

            var focus = Assert.Single(top.Rows.Take(1)).Function;
            var callerCallee = cpu.CpuCallerCallee(
                CpuFixturePath,
                focus,
                top: 1000,
                pid: lifetime.Key.Process.Pid,
                resolveSymbols: false,
                tid: lifetime.Key.Tid,
                processStartUs: lifetime.Key.Process.StartUs,
                threadStartUs: lifetime.StartUs,
                threadGeneration: lifetime.Key.Generation);

            Assert.Equal(lifetime.Key, callerCallee.SelectedThread);
            Assert.Equal([Candidate(lifetime)], callerCallee.IncludedThreads);
            Assert.Equal(top.TotalSamples, callerCallee.SourceTotalMetric);
            Assert.Equal(top.HasSampledProfileStacks, callerCallee.HasSampledProfileStacks);

            var symbolized = cpu.CpuTopFunctions(
                CpuFixturePath,
                top: 1000,
                pid: lifetime.Key.Process.Pid,
                resolveSymbols: true,
                tid: lifetime.Key.Tid,
                processStartUs: lifetime.Key.Process.StartUs,
                threadStartUs: lifetime.StartUs,
                threadGeneration: lifetime.Key.Generation);
            Assert.Equal(top.SelectedThread, symbolized.SelectedThread);
            Assert.Equal(top.TotalSamples, symbolized.TotalSamples);
        });
    }

    private static ThreadLifetime FindBlockedThread(TraceCache cache)
    {
        var trace = cache.Get(WaitFixturePath);
        var identities = TraceIdentityIndex.For(trace);
        var window = new TimeWindow(0, identities.TraceEndUs);
        var aggregate = WaitAnalysis.Analyze(
            trace, top: int.MaxValue, pid: null, startUs: null, endUs: null);

        foreach (var row in aggregate.Rows.Where(row => row.BlockedUs > 0))
        {
            foreach (var lifetime in identities.Threads.Lifetimes.Where(lifetime =>
                         lifetime.Key.Process.Pid == row.Pid &&
                         lifetime.Key.Tid == row.Tid &&
                         lifetime.Intersects(window)))
            {
                var process = identities.Processes.Lifetimes.Single(candidate =>
                    candidate.Key == lifetime.Key.Process);
                var scope = new ThreadAnalysisScope(
                    window,
                    row.Pid,
                    process,
                    lifetime,
                    AggregatesPidLifetimes: false,
                    PidReuseObserved: false);
                if (WaitAnalysis.Analyze(trace, top: int.MaxValue, scope).TotalBlockedUs > 0)
                    return lifetime;
            }
        }

        throw new InvalidOperationException("The wait-bound fixture has no blocked thread instance.");
    }

    private static ThreadLifetime FindSampledThread(TraceCache cache)
    {
        var trace = cache.Get(CpuFixturePath);
        var identities = TraceIdentityIndex.For(trace);
        foreach (var traceEvent in trace.Events)
        {
            if (traceEvent is not SampledProfileTraceData sample ||
                sample.ProcessID <= 0 || sample.ThreadID <= 0)
            {
                continue;
            }

            var timestampUs = TraceTime.FromMilliseconds(sample.TimeStampRelativeMSec);
            var candidates = identities.Threads.Lifetimes.Where(lifetime =>
                lifetime.Key.Process.Pid == sample.ProcessID &&
                lifetime.Key.Tid == sample.ThreadID &&
                lifetime.StartUs <= timestampUs && timestampUs < lifetime.EndUs).ToArray();
            if (candidates.Length == 1)
                return candidates[0];
        }

        throw new InvalidOperationException("The CPU fixture has no resolvable sampled thread instance.");
    }

    private static ProcessInstanceKey FindExistingProcess(TraceCache cache, string path) =>
        TraceIdentityIndex.For(cache.Get(path)).Processes.Lifetimes
            .Where(lifetime => lifetime.Key.Pid > 0)
            .Select(lifetime => lifetime.Key)
            .First();

    private static ThreadScopeCandidate Candidate(ThreadLifetime lifetime) =>
        new(lifetime.Key, lifetime.StartUs, lifetime.EndUs);

    private static TraceIdentityIndex ReusedStackIdentityIndex() =>
        TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 400,
            processes:
            [
                new ProcessLifetime(
                    new ProcessInstanceKey(50, 0), 100,
                    StartObserved: true, EndObserved: true),
                new ProcessLifetime(
                    new ProcessInstanceKey(50, 200), 400,
                    StartObserved: true, EndObserved: false),
            ],
            threads:
            [
                new ThreadLifecycleEvent(50, 7, 10, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(50, 7, 90, ThreadLifecycleEventKind.Stop, Observed: true),
                new ThreadLifecycleEvent(50, 7, 210, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(50, 7, 250, ThreadLifecycleEventKind.Stop, Observed: true),
                new ThreadLifecycleEvent(50, 7, 260, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(50, 7, 390, ThreadLifecycleEventKind.Stop, Observed: true),
            ]);

    private static void AssertMissingProcess(CpuTopFunctionsResponse response) =>
        AssertScopeFailure(
            response.ScopeStatus,
            response.NoDataReason,
            response.IncludedProcesses,
            response.IncludedThreads,
            response.Warnings,
            "scope_not_found",
            "scope_not_found",
            expectedWarningCode: "process_instance_not_found");

    private static void AssertMissingProcess(CpuPreciseResponse response) =>
        AssertScopeFailure(
            response.ScopeStatus,
            response.NoDataReason,
            response.IncludedProcesses,
            response.IncludedThreads,
            response.Warnings,
            "scope_not_found",
            "scope_not_found",
            expectedWarningCode: "process_instance_not_found");

    private static void AssertMissingProcess(WaitAnalysisResponse response) =>
        AssertScopeFailure(
            response.ScopeStatus,
            response.NoDataReason,
            response.IncludedProcesses,
            response.IncludedThreads,
            response.Warnings,
            "scope_not_found",
            "scope_not_found",
            expectedWarningCode: "process_instance_not_found");

    private static void AssertMissingProcess(WaitTopStacksResponse response) =>
        AssertScopeFailure(
            response.ScopeStatus,
            response.NoDataReason,
            response.IncludedProcesses,
            response.IncludedThreads,
            response.Warnings,
            "scope_not_found",
            "scope_not_found",
            expectedWarningCode: "process_instance_not_found");

    private static void AssertMissingProcess(CallerCalleeResponse response) =>
        AssertScopeFailure(
            response.ScopeStatus,
            response.NoDataReason,
            response.IncludedProcesses,
            response.IncludedThreads,
            response.Warnings,
            "scope_not_found",
            "scope_not_found",
            expectedWarningCode: "process_instance_not_found");

    private static void AssertMissingThread(
        CpuTopFunctionsResponse response,
        ProcessInstanceKey process) =>
        AssertScopeFailure(
            response.ScopeStatus,
            response.NoDataReason,
            response.IncludedProcesses,
            response.IncludedThreads,
            response.Warnings,
            "scope_not_found",
            "scope_not_found",
            [process],
            "thread_instance_not_found");

    private static void AssertMissingThread(
        CpuPreciseResponse response,
        ProcessInstanceKey process) =>
        AssertScopeFailure(
            response.ScopeStatus,
            response.NoDataReason,
            response.IncludedProcesses,
            response.IncludedThreads,
            response.Warnings,
            "scope_not_found",
            "scope_not_found",
            [process],
            "thread_instance_not_found");

    private static void AssertMissingThread(
        WaitAnalysisResponse response,
        ProcessInstanceKey process) =>
        AssertScopeFailure(
            response.ScopeStatus,
            response.NoDataReason,
            response.IncludedProcesses,
            response.IncludedThreads,
            response.Warnings,
            "scope_not_found",
            "scope_not_found",
            [process],
            "thread_instance_not_found");

    private static void AssertMissingThread(
        WaitTopStacksResponse response,
        ProcessInstanceKey process) =>
        AssertScopeFailure(
            response.ScopeStatus,
            response.NoDataReason,
            response.IncludedProcesses,
            response.IncludedThreads,
            response.Warnings,
            "scope_not_found",
            "scope_not_found",
            [process],
            "thread_instance_not_found");

    private static void AssertMissingThread(
        CallerCalleeResponse response,
        ProcessInstanceKey process) =>
        AssertScopeFailure(
            response.ScopeStatus,
            response.NoDataReason,
            response.IncludedProcesses,
            response.IncludedThreads,
            response.Warnings,
            "scope_not_found",
            "scope_not_found",
            [process],
            "thread_instance_not_found");

    private static void AssertScopeFailure(
        string scopeStatus,
        string? noDataReason,
        IReadOnlyList<ProcessInstanceKey>? includedProcesses,
        IReadOnlyList<ThreadScopeCandidate>? includedThreads,
        IReadOnlyList<string> warnings,
        string expectedStatus,
        string expectedReason,
        IReadOnlyList<ProcessInstanceKey>? expectedProcesses = null,
        string? expectedWarningCode = null)
    {
        Assert.Equal(expectedStatus, scopeStatus);
        Assert.Equal(expectedReason, noDataReason);
        Assert.Equal(expectedProcesses ?? [], includedProcesses ?? []);
        Assert.Empty(includedThreads ?? []);
        Assert.Contains(warnings, warning =>
            warning.StartsWith((expectedWarningCode ?? expectedReason) + ":"));
    }

    private static void AssertAmbiguousThread(
        ThreadAnalysisScope scope,
        IReadOnlyList<ThreadScopeCandidate> candidates)
    {
        Assert.False(scope.IsResolved);
        Assert.False(scope.MatchesPoint(50, 7, 50));
        var coverage = new DomainStackCoverageAccumulator("synthetic").Snapshot();
        var contract = StackResultContract.FromThreadScope(
            scope, filterSpecified: true, coverage);
        var warnings = new List<string>();
        contract.AddWarning(warnings);
        AssertAmbiguousThread(
            contract.ScopeStatus,
            contract.NoDataReason,
            sourceTotal: 0,
            contract.IncludedProcesses,
            contract.IncludedThreads,
            warnings,
            candidates);
    }

    private static void AssertAmbiguousThread(
        CpuPreciseResponse response,
        IReadOnlyList<ThreadScopeCandidate> candidates) =>
        AssertAmbiguousThread(
            response.ScopeStatus,
            response.NoDataReason,
            response.TotalCpuUs,
            response.IncludedProcesses,
            response.IncludedThreads,
            response.Warnings,
            candidates);

    private static void AssertAmbiguousThread(
        WaitAnalysisResponse response,
        IReadOnlyList<ThreadScopeCandidate> candidates) =>
        AssertAmbiguousThread(
            response.ScopeStatus,
            response.NoDataReason,
            response.TotalBlockedUs,
            response.IncludedProcesses,
            response.IncludedThreads,
            response.Warnings,
            candidates);

    private static void AssertAmbiguousThread(
        string scopeStatus,
        string? noDataReason,
        long sourceTotal,
        IReadOnlyList<ProcessInstanceKey>? includedProcesses,
        IReadOnlyList<ThreadScopeCandidate>? includedThreads,
        IReadOnlyList<string> warnings,
        IReadOnlyList<ThreadScopeCandidate> candidates)
    {
        Assert.Equal("ambiguous_thread_instance", scopeStatus);
        Assert.Equal("ambiguous_thread_instance", noDataReason);
        Assert.Equal(0, sourceTotal);
        Assert.Equal(
            candidates.Select(candidate => candidate.Thread.Process).Distinct().OrderBy(key => key.StartUs),
            (includedProcesses ?? []).OrderBy(key => key.StartUs));
        Assert.Equal(
            candidates.OrderBy(key => key.Thread.Process.StartUs).ThenBy(key => key.ThreadStartUs),
            (includedThreads ?? []).OrderBy(key => key.Thread.Process.StartUs).ThenBy(key => key.ThreadStartUs));
        Assert.All(includedThreads ?? [], candidate =>
            Assert.True(candidate.ThreadStartUs < candidate.ThreadEndUs));
        Assert.Contains(warnings, warning =>
            warning.StartsWith("ambiguous_thread_instance:") &&
            warning.Contains("threadStartUs=") &&
            warning.Contains("threadGeneration="));
    }

    private static void AssertPidAggregate(
        ThreadAnalysisScope scope,
        IReadOnlyList<ProcessInstanceKey> processes)
    {
        Assert.True(scope.IsResolved);
        Assert.True(scope.MatchesPoint(50, 7, 50));
        Assert.True(scope.MatchesPoint(50, 7, 300));
        var coverage = new DomainStackCoverageAccumulator("synthetic").Snapshot();
        var contract = StackResultContract.FromThreadScope(
            scope, filterSpecified: true, coverage);
        AssertPidAggregate(
            contract.ScopeStatus,
            contract.ScopeMode,
            contract.PidReuseObserved,
            contract.IncludedProcesses,
            processes);
    }

    private static void AssertPidAggregate(
        string scopeStatus,
        string scopeMode,
        bool pidReuseObserved,
        IReadOnlyList<ProcessInstanceKey>? includedProcesses,
        IReadOnlyList<ProcessInstanceKey> processes)
    {
        Assert.Equal("ok", scopeStatus);
        Assert.Equal("pid_aggregate", scopeMode);
        Assert.True(pidReuseObserved);
        Assert.Equal(
            processes.OrderBy(key => key.StartUs),
            (includedProcesses ?? []).OrderBy(key => key.StartUs));
    }

    private static void WithSymbolPathUnset(Action action)
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", null);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", saved);
        }
    }
}
