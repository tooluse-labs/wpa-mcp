using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public sealed class ThreadScopedCpuWaitTests
{
    private const string CpuFixturePath = "fixtures/small_cpu.etl";
    private const string WaitFixturePath = "fixtures/small_wait_bound.etl";

    [Fact]
    public void SixThreadTools_RejectTidWithoutPidBeforeTraceAccess()
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
                threadStartUs: lifetime.StartUs);
            var stacks = wait.WaitTopStacks(
                WaitFixturePath,
                top: 10,
                pid: lifetime.Key.Process.Pid,
                whenBuckets: 17,
                resolveSymbols: false,
                tid: lifetime.Key.Tid,
                processStartUs: lifetime.Key.Process.StartUs,
                threadStartUs: lifetime.StartUs);

            Assert.Equal(lifetime.Key, summary.SelectedThread);
            var summaryRow = Assert.Single(summary.Rows);
            Assert.True(summary.HasContextSwitches);
            Assert.True(summary.HasContextSwitchBlockingStacks);
            Assert.Equal(lifetime.Key, stacks.SelectedThread);
            Assert.Equal(summary.TotalBlockedUs, stacks.TotalBlockedUs);
            Assert.Equal(stacks.TotalBlockedUs, stacks.When!.Buckets.Sum());
            Assert.True(stacks.HasContextSwitches);
            Assert.True(stacks.HasContextSwitchBlockingStacks);
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
                threadStartUs: lifetime.StartUs);

            Assert.Equal(lifetime.Key, callerCallee.SelectedThread);
            Assert.Equal(summary.TotalBlockedUs, callerCallee.SourceTotalMetric);
            Assert.Equal(stacks.UnmatchedBlockedIntervalCount, callerCallee.UnmatchedIntervalCount);
            Assert.True(callerCallee.HasContextSwitchBlockingStacks);

            var precise = cpu.CpuPreciseAnalysis(
                WaitFixturePath,
                top: 1,
                pid: lifetime.Key.Process.Pid,
                tid: lifetime.Key.Tid,
                processStartUs: lifetime.Key.Process.StartUs,
                threadStartUs: lifetime.StartUs);
            Assert.Equal(lifetime.Key, precise.SelectedThread);
            Assert.Equal(lifetime.Key.Tid, Assert.Single(precise.Rows).Tid);
            Assert.Equal(summaryRow.CpuUs, precise.TotalCpuUs);

            var symbolized = wait.WaitTopStacks(
                WaitFixturePath,
                top: 10,
                pid: lifetime.Key.Process.Pid,
                resolveSymbols: true,
                tid: lifetime.Key.Tid,
                processStartUs: lifetime.Key.Process.StartUs,
                threadStartUs: lifetime.StartUs);
            Assert.Equal(stacks.SelectedThread, symbolized.SelectedThread);
            Assert.Equal(stacks.TotalBlockedUs, symbolized.TotalBlockedUs);
            Assert.Equal(stacks.SampleCount, symbolized.SampleCount);
        });
    }

    [Fact]
    public void ExactSelectorFailure_PreservesStableInternalCode()
    {
        var wait = new WaitTools(new TraceCache(capacity: 1));
        var missingThread = Assert.Throws<ThreadScopeResolutionException>(() =>
            wait.WaitAnalysis(WaitFixturePath, pid: int.MaxValue, tid: int.MaxValue));
        Assert.Equal("thread_instance_not_found", missingThread.Code);

        var missingProcess = Assert.Throws<ThreadScopeResolutionException>(() =>
            wait.WaitAnalysis(
                WaitFixturePath,
                pid: int.MaxValue,
                processStartUs: long.MaxValue));
        Assert.Equal("process_instance_not_found", missingProcess.Code);
    }

    [Fact]
    public void ExactNewWaitInstanceCountsOnlyItsSideAtReuseBoundary()
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
        Assert.Equal(1, row.ContextSwitches);
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
                threadStartUs: lifetime.StartUs);

            Assert.Equal(lifetime.Key, top.SelectedThread);
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
                threadStartUs: lifetime.StartUs);

            Assert.Equal(lifetime.Key, callerCallee.SelectedThread);
            Assert.Equal(top.TotalSamples, callerCallee.SourceTotalMetric);
            Assert.Equal(top.HasSampledProfileStacks, callerCallee.HasSampledProfileStacks);

            var symbolized = cpu.CpuTopFunctions(
                CpuFixturePath,
                top: 1000,
                pid: lifetime.Key.Process.Pid,
                resolveSymbols: true,
                tid: lifetime.Key.Tid,
                processStartUs: lifetime.Key.Process.StartUs,
                threadStartUs: lifetime.StartUs);
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
