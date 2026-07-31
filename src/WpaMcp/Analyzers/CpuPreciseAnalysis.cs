using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// CPU Usage (Precise)-style summary from CSwitch + ReadyThread events. Scheduler
// state is maintained for the full trace and exact process/thread identities; the
// requested scope is applied only when a closed interval or point is projected.
public static class CpuPreciseAnalysis
{
    public static CpuPreciseResponse Analyze(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs)
    {
        var scope = ResolveLegacyScope(trace, pid, startUs, endUs);
        return Analyze(trace, top, scope);
    }

    internal static CpuPreciseResponse Analyze(
        TraceLog trace,
        int top,
        ThreadAnalysisScope scope)
    {
        var identities = TraceIdentityIndex.For(trace);
        var accumulator = new CpuPreciseAccumulator(
            top, scope, identities.TraceEndUs, identities.Threads.EndUsFor);
        long unresolvedIdentityCount = 0;

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ThreadCSwitch += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var switchResolution = identities.Threads.ResolveSwitch(
                    data.OldProcessID,
                    data.OldThreadID,
                    data.NewProcessID,
                    data.NewThreadID,
                    timestampUs);
                var oldResolution = switchResolution.OldThread;
                var newResolution = switchResolution.NewThread;
                unresolvedIdentityCount += CountUnresolvedSide(
                    data.OldProcessID, data.OldThreadID, oldResolution);
                unresolvedIdentityCount += CountUnresolvedSide(
                    data.NewProcessID, data.NewThreadID, newResolution);

                accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
                    OldThread: ResolvedValue(oldResolution),
                    OldProcessName: data.OldProcessName ?? string.Empty,
                    OldThreadWaitReason: data.OldThreadWaitReason,
                    NewThread: ResolvedValue(newResolution),
                    NewProcessName: data.NewProcessName ?? string.Empty,
                    ProcessorNumber: data.ProcessorNumber,
                    TimestampUs: timestampUs));
            };

            kernel.DispatcherReadyThread += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var resolution = identities.Threads.ResolveAt(
                    data.AwakenedProcessID,
                    data.AwakenedThreadID,
                    timestampUs);
                unresolvedIdentityCount += CountUnresolvedSide(
                    data.AwakenedProcessID,
                    data.AwakenedThreadID,
                    resolution);
                accumulator.ProcessReady(new CpuPreciseResolvedReadyEvent(
                    ResolvedValue(resolution), timestampUs));
            };

            kernel.ThreadStop += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var resolution = identities.Threads.ResolveAtEndpoint(
                    data.ProcessID,
                    data.ThreadID,
                    timestampUs,
                    preferredEndObserved: true);
                var thread = ResolvedValue(resolution);
                if (thread.HasValue)
                {
                    accumulator.ProcessStop(thread.Value, timestampUs);
                }
                else
                {
                    unresolvedIdentityCount += CountUnresolvedSide(
                        data.ProcessID, data.ThreadID, resolution);
                }
            };
        });

        accumulator.ReportUnresolvedIdentity(unresolvedIdentityCount);
        return accumulator.BuildResponse();
    }

    private static ThreadAnalysisScope ResolveLegacyScope(
        TraceLog trace,
        int? pid,
        long? startUs,
        long? endUs)
    {
        var window = Validation.RequireWindowInput(startUs, endUs).Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds),
            maxDurationUs: null);
        var resolution = ThreadAnalysisScope.Resolve(
            window,
            pid,
            tid: null,
            processStartUs: null,
            threadStartUs: null,
            TraceIdentityIndex.For(trace));
        return resolution.Status == InstanceResolutionStatus.Resolved &&
               resolution.Value.HasValue
            ? resolution.Value.Value
            : throw new InvalidOperationException(
                $"Unable to resolve precise CPU scope: {resolution.Status}.");
    }

    private static ThreadInstanceKey? ResolvedValue(
        InstanceResolution<ThreadInstanceKey> resolution) =>
        resolution.Status == InstanceResolutionStatus.Resolved && resolution.Value.HasValue
            ? resolution.Value.Value
            : null;

    private static int CountUnresolvedSide(
        int pid,
        int tid,
        InstanceResolution<ThreadInstanceKey> resolution) =>
        pid > 0 && tid > 0 && resolution.Status != InstanceResolutionStatus.Resolved
            ? 1
            : 0;
}

internal readonly record struct CpuPreciseSwitchEvent(
    int OldProcessId,
    string OldProcessName,
    int OldThreadId,
    ThreadWaitReason OldThreadWaitReason,
    int NewProcessId,
    string NewProcessName,
    int NewThreadId,
    int ProcessorNumber,
    double TimeStampRelativeMSec);

internal readonly record struct CpuPreciseReadyEvent(
    int AwakenedProcessId,
    int AwakenedThreadId,
    double TimeStampRelativeMSec);

internal readonly record struct CpuPreciseResolvedSwitchEvent(
    ThreadInstanceKey? OldThread,
    string OldProcessName,
    ThreadWaitReason OldThreadWaitReason,
    ThreadInstanceKey? NewThread,
    string NewProcessName,
    int ProcessorNumber,
    long TimestampUs);

internal readonly record struct CpuPreciseResolvedReadyEvent(
    ThreadInstanceKey? Thread,
    long TimestampUs);

internal sealed class CpuPreciseAccumulator
{
    private readonly int _top;
    private readonly ThreadAnalysisScope _scope;
    private readonly long _traceEndUs;
    private readonly bool _seedFirstObservedRunningInterval;
    private readonly SchedulerIntervalAccumulator _scheduler;
    private readonly Dictionary<ThreadInstanceKey, ThreadStats> _threads = new();
    private readonly Dictionary<ThreadInstanceKey, long> _pendingReadyUs = new();
    private readonly Dictionary<int, ThreadInstanceKey> _knownRunningByCore = new();
    private readonly HashSet<ThreadInstanceKey> _seenThreads = new();
    private readonly HashSet<int> _seenSwitchOutCores = new();

    private long _traceCSwitches;
    private long _windowCSwitches;
    private long _traceReadyEvents;
    private long _windowReadyEvents;
    private long _skippedUnmatchedSwitchOuts;
    private long _droppedStaleCoreIntervals;
    private long _unresolvedIdentityCount;
    private bool _built;

    public CpuPreciseAccumulator(
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        long? traceEndUs = null)
    {
        _top = top;
        var effectiveTraceEndUs = traceEndUs ?? endUs ?? long.MaxValue;
        var window = new TimeWindow(startUs ?? 0, endUs ?? effectiveTraceEndUs);
        _scope = new ThreadAnalysisScope(
            window,
            pid,
            Process: null,
            Thread: null,
            AggregatesPidLifetimes: pid.HasValue,
            PidReuseObserved: false);
        _traceEndUs = effectiveTraceEndUs;
        _seedFirstObservedRunningInterval = true;
        _scheduler = new SchedulerIntervalAccumulator();
    }

    internal CpuPreciseAccumulator(
        int top,
        ThreadAnalysisScope scope,
        long traceEndUs,
        Func<ThreadInstanceKey, long?>? threadEndUs = null)
    {
        if (traceEndUs <= 0)
            throw new ArgumentOutOfRangeException(nameof(traceEndUs));

        _top = top;
        _scope = scope;
        _traceEndUs = traceEndUs;
        _seedFirstObservedRunningInterval = false;
        _scheduler = new SchedulerIntervalAccumulator(threadEndUs);
    }

    public void ProcessReady(CpuPreciseReadyEvent data)
    {
        var thread = TryMakeSyntheticKey(
            data.AwakenedProcessId, data.AwakenedThreadId, out var key)
            ? key
            : (ThreadInstanceKey?)null;
        ProcessReady(new CpuPreciseResolvedReadyEvent(
            thread,
            TraceTime.FromMilliseconds(data.TimeStampRelativeMSec)));
    }

    internal void ProcessReady(CpuPreciseResolvedReadyEvent data)
    {
        EnsureMutable();
        _traceReadyEvents++;
        if (!data.Thread.HasValue)
            return;

        var thread = data.Thread.Value;
        if (_scope.MatchesPoint(thread, data.TimestampUs))
        {
            _windowReadyEvents++;
        }

        _seenThreads.Add(thread);
        GetStats(thread, processName: string.Empty);
        _pendingReadyUs.TryAdd(thread, data.TimestampUs);
    }

    public void ProcessCSwitch(CpuPreciseSwitchEvent data)
    {
        var oldThread = TryMakeSyntheticKey(
            data.OldProcessId, data.OldThreadId, out var oldKey)
            ? oldKey
            : (ThreadInstanceKey?)null;
        var newThread = TryMakeSyntheticKey(
            data.NewProcessId, data.NewThreadId, out var newKey)
            ? newKey
            : (ThreadInstanceKey?)null;
        ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            oldThread,
            data.OldProcessName,
            data.OldThreadWaitReason,
            newThread,
            data.NewProcessName,
            data.ProcessorNumber,
            TraceTime.FromMilliseconds(data.TimeStampRelativeMSec)));
    }

    internal void ProcessCSwitch(CpuPreciseResolvedSwitchEvent data)
    {
        EnsureMutable();
        _traceCSwitches++;
        if (MatchesPoint(data.OldThread, data.TimestampUs) ||
            MatchesPoint(data.NewThread, data.TimestampUs))
        {
            _windowCSwitches++;
        }

        var core = data.ProcessorNumber;
        var hadRunning = _knownRunningByCore.TryGetValue(core, out var expectedOldThread);
        var firstSwitchOutOnCore = _seenSwitchOutCores.Add(core);
        var firstObservedOldThread = false;

        if (data.OldThread.HasValue)
        {
            var oldThread = data.OldThread.Value;
            firstObservedOldThread = _seenThreads.Add(oldThread);
            var oldStats = GetStats(oldThread, data.OldProcessName);
            if (MatchesPoint(oldThread, data.TimestampUs))
            {
                oldStats.ContextSwitches++;
                var reason = WaitAnalysis.WaitReasonName(data.OldThreadWaitReason);
                if (reason == "WrQuantumEnd")
                    oldStats.QuantumEndSwitches++;
                if (reason is "WrPreempted" or "WrDeferredPreempt")
                    oldStats.PreemptedSwitches++;
            }
        }

        if (data.NewThread.HasValue)
        {
            var newThread = data.NewThread.Value;
            _seenThreads.Add(newThread);
            var newStats = GetStats(newThread, data.NewProcessName);
            if (MatchesPoint(newThread, data.TimestampUs))
                newStats.ContextSwitches++;
        }

        var closed = _scheduler.ProcessSwitch(
            data.OldThread,
            data.NewThread,
            data.TimestampUs,
            WaitAnalysis.WaitReasonName(data.OldThreadWaitReason),
            core);
        if (closed.Running.HasValue)
        {
            AccountRunning(closed.Running.Value);
        }
        else if (data.OldThread.HasValue)
        {
            if (_seedFirstObservedRunningInterval &&
                firstSwitchOutOnCore &&
                firstObservedOldThread)
            {
                AccountRunning(new RunningInterval(
                    data.OldThread.Value,
                    _scope.Window.StartUs,
                    data.TimestampUs,
                    core));
            }
            else if (!hadRunning)
            {
                _skippedUnmatchedSwitchOuts++;
            }
        }

        if (hadRunning &&
            (!data.OldThread.HasValue || data.OldThread.Value != expectedOldThread))
        {
            _droppedStaleCoreIntervals++;
        }

        if (data.NewThread.HasValue)
        {
            foreach (var duplicate in _knownRunningByCore
                         .Where(item =>
                             item.Key != core && item.Value == data.NewThread.Value)
                         .Select(item => item.Key)
                         .ToArray())
            {
                _knownRunningByCore.Remove(duplicate);
            }
            _knownRunningByCore[core] = data.NewThread.Value;
            AccountReady(data.NewThread.Value, data.TimestampUs);
        }
        else
        {
            _knownRunningByCore.Remove(core);
        }
    }

    internal void ProcessStop(ThreadInstanceKey thread, long timestampUs)
    {
        EnsureMutable();
        var closed = _scheduler.Stop(thread, timestampUs);
        if (closed.Running.HasValue)
            AccountRunning(closed.Running.Value);
        _pendingReadyUs.Remove(thread);
        foreach (var core in _knownRunningByCore
                     .Where(item => item.Value == thread)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _knownRunningByCore.Remove(core);
        }
    }

    internal void ReportUnresolvedIdentity(long count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        _unresolvedIdentityCount = checked(_unresolvedIdentityCount + count);
    }

    public CpuPreciseResponse BuildResponse()
    {
        EnsureMutable();
        _built = true;
        var completion = _scheduler.Complete(_traceEndUs);
        foreach (var interval in completion.ClosedAtTraceEnd)
            AccountRunning(interval);
        _knownRunningByCore.Clear();

        if (_scope.Thread is not null)
            GetStats(_scope.Thread.Key, processName: string.Empty);

        var matchingStats = _threads
            .Where(item => _scope.MatchesThread(item.Key));
        var projectedRows = _scope.Thread is not null
            ? matchingStats.Select(item => ToRow(
                item.Key.Process.Pid,
                item.Key.Tid,
                item.Value))
            : matchingStats
                .GroupBy(item => new RawThreadKey(item.Key.Process.Pid, item.Key.Tid))
                .Select(group => ToRow(
                    group.Key.Pid,
                    group.Key.Tid,
                    MergeStats(group
                        .OrderBy(item => item.Key.Process.StartUs)
                        .ThenBy(item => item.Key.Generation)
                        .Select(item => item.Value))));
        var allRows = projectedRows
            .Where(row =>
                _scope.Thread is not null ||
                row.CpuUs > 0 || row.ReadyCount > 0 || row.ContextSwitches > 0)
            .OrderByDescending(row => row.CpuUs)
            .ThenByDescending(row => row.ReadyLatencyUs)
            .ThenBy(row => row.Pid)
            .ThenBy(row => row.Tid)
            .ToList();
        var rows = _scope.Thread is not null
            ? allRows
            : allRows.Take(_top).ToList();

        var warnings = BuildWarnings(completion, allRows);
        if (_scope.PidReuseObserved)
        {
            warnings.Add(
                "ambiguous_process_instance: pid-only scope aggregates multiple process lifetimes.");
        }
        return new CpuPreciseResponse(
            Rows: rows,
            TotalCpuUs: allRows.Sum(row => row.CpuUs),
            TotalContextSwitches: _windowCSwitches,
            TotalReadyCount: allRows.Sum(row => row.ReadyCount),
            TotalReadyLatencyUs: allRows.Sum(row => row.ReadyLatencyUs),
            Warnings: warnings,
            SelectedProcess: _scope.Process?.Key,
            SelectedThread: _scope.Thread?.Key,
            HasContextSwitches: _traceCSwitches > 0,
            HasSampledProfileStacks: false,
            SymbolResolutionState: "not_applicable");
    }

    private void AccountRunning(RunningInterval interval)
    {
        var cpuUs = _scope.AccountInterval(
            interval.Thread, interval.StartUs, interval.EndUs);
        if (cpuUs <= 0)
            return;

        var stats = GetStats(interval.Thread, processName: string.Empty);
        stats.CpuUs = checked(stats.CpuUs + cpuUs);
        if (interval.Core >= 0)
        {
            stats.CoreCpuUs[interval.Core] = checked(
                stats.CoreCpuUs.GetValueOrDefault(interval.Core) + cpuUs);
        }
    }

    private void AccountReady(ThreadInstanceKey thread, long switchInUs)
    {
        if (!_pendingReadyUs.Remove(thread, out var readyUs))
            return;

        var latencyUs = _scope.AccountInterval(thread, readyUs, switchInUs);
        if (latencyUs <= 0)
            return;

        var stats = GetStats(thread, processName: string.Empty);
        stats.ReadyCount++;
        stats.ReadyLatencyUs = checked(stats.ReadyLatencyUs + latencyUs);
        stats.MaxReadyLatencyUs = Math.Max(stats.MaxReadyLatencyUs ?? 0, latencyUs);
    }

    private ThreadStats GetStats(ThreadInstanceKey key, string processName)
    {
        if (!_threads.TryGetValue(key, out var stats))
            _threads[key] = stats = new ThreadStats();
        if (!string.IsNullOrEmpty(processName))
            stats.ProcessName = processName;
        return stats;
    }

    private static ThreadStats MergeStats(IEnumerable<ThreadStats> sources)
    {
        var aggregate = new ThreadStats();
        foreach (var source in sources)
        {
            if (string.IsNullOrEmpty(aggregate.ProcessName) &&
                !string.IsNullOrEmpty(source.ProcessName))
            {
                aggregate.ProcessName = source.ProcessName;
            }

            aggregate.CpuUs = checked(aggregate.CpuUs + source.CpuUs);
            aggregate.ContextSwitches = checked(
                aggregate.ContextSwitches + source.ContextSwitches);
            aggregate.ReadyCount = checked(aggregate.ReadyCount + source.ReadyCount);
            aggregate.ReadyLatencyUs = checked(
                aggregate.ReadyLatencyUs + source.ReadyLatencyUs);
            if (source.MaxReadyLatencyUs.HasValue)
            {
                aggregate.MaxReadyLatencyUs = Math.Max(
                    aggregate.MaxReadyLatencyUs ?? 0,
                    source.MaxReadyLatencyUs.Value);
            }
            aggregate.QuantumEndSwitches = checked(
                aggregate.QuantumEndSwitches + source.QuantumEndSwitches);
            aggregate.PreemptedSwitches = checked(
                aggregate.PreemptedSwitches + source.PreemptedSwitches);
            foreach (var core in source.CoreCpuUs)
            {
                aggregate.CoreCpuUs[core.Key] = checked(
                    aggregate.CoreCpuUs.GetValueOrDefault(core.Key) + core.Value);
            }
        }

        return aggregate;
    }

    private static CpuPreciseThreadRow ToRow(
        int pid,
        int tid,
        ThreadStats stats)
    {
        var topCores = stats.CoreCpuUs
            .OrderByDescending(item => item.Value)
            .Take(8)
            .Select(item => new CpuCoreBucket(
                Core: item.Key,
                CpuUs: item.Value,
                CpuPct: StackSourceTopN.Pct(stats.CpuUs, item.Value)))
            .ToList();

        return new CpuPreciseThreadRow(
            Pid: pid,
            ProcessName: stats.ProcessName,
            Tid: tid,
            CpuUs: stats.CpuUs,
            ContextSwitches: stats.ContextSwitches,
            ReadyCount: stats.ReadyCount,
            ReadyLatencyUs: stats.ReadyLatencyUs,
            AvgReadyLatencyUs: stats.ReadyCount > 0
                ? (double)stats.ReadyLatencyUs / stats.ReadyCount
                : null,
            MaxReadyLatencyUs: stats.MaxReadyLatencyUs,
            PrimaryCore: topCores.Count > 0 ? topCores[0].Core : null,
            TopCores: topCores,
            QuantumEndSwitches: stats.QuantumEndSwitches,
            PreemptedSwitches: stats.PreemptedSwitches);
    }

    private List<string> BuildWarnings(
        SchedulerIntervalResult completion,
        IReadOnlyList<CpuPreciseThreadRow> allRows)
    {
        var warnings = new List<string>();
        if (_traceCSwitches == 0)
        {
            warnings.Add(
                "No CSwitch events found. The capture profile must include the CSwitch keyword. " +
                "Default WPR 'CPU' / 'CPU.light' profiles include it; some custom .wprp files may not.");
        }
        else if (_windowCSwitches == 0 &&
                 allRows.All(row => row.CpuUs == 0 && row.ContextSwitches == 0))
        {
            warnings.Add(
                "CSwitch events were present in the trace, but none matched the requested pid/thread/window scope.");
        }

        if (_traceReadyEvents == 0)
        {
            warnings.Add(
                "No DispatcherReadyThread events found. Ready latency cannot be computed without ReadyThread events.");
        }
        else if (_windowReadyEvents == 0 && allRows.All(row => row.ReadyCount == 0))
        {
            warnings.Add(
                "ReadyThread events were present in the trace, but none matched the requested pid/thread/window scope.");
        }

        var schedulerOnlyUnmatched = Math.Max(
            0,
            completion.UnmatchedRunningIntervalCount - completion.IdentityMismatchCount);
        var unmatchedRunning = Math.Max(
            _skippedUnmatchedSwitchOuts,
            schedulerOnlyUnmatched);
        if (unmatchedRunning > 0)
        {
            warnings.Add(
                $"Skipped {unmatchedRunning:N0} unmatched CSwitch old-thread interval(s) that could not be tied to a prior switch-in or a unique trace-start seed. " +
                "This avoids over-counting CPU time when scheduler state is incomplete or thread IDs are reused.");
        }

        var staleCoreIntervals = Math.Max(
            _droppedStaleCoreIntervals,
            completion.IdentityMismatchCount);
        if (staleCoreIntervals > 0)
        {
            warnings.Add(
                $"Dropped {staleCoreIntervals:N0} stale per-core running interval(s) after later CSwitch data showed a different old thread on that processor. " +
                "This keeps CPU accounting bounded to one running thread per processor.");
        }

        if (_unresolvedIdentityCount > 0)
        {
            warnings.Add(
                $"scheduler_identity_unresolved: {_unresolvedIdentityCount:N0} event-side identity resolution(s) were unavailable or ambiguous.");
        }
        return warnings;
    }

    private bool MatchesPoint(ThreadInstanceKey? thread, long timestampUs) =>
        thread.HasValue && MatchesPoint(thread.Value, timestampUs);

    private bool MatchesPoint(ThreadInstanceKey thread, long timestampUs) =>
        _scope.MatchesPoint(thread, timestampUs);

    private static bool TryMakeSyntheticKey(
        int pid,
        int tid,
        out ThreadInstanceKey key)
    {
        key = new ThreadInstanceKey(
            new ProcessInstanceKey(pid, StartUs: 0),
            tid,
            Generation: 1);
        return pid > 0 && tid != 0;
    }

    private void EnsureMutable()
    {
        if (_built)
            throw new InvalidOperationException("CPU precise accumulator is complete.");
    }

    private sealed class ThreadStats
    {
        public string ProcessName { get; set; } = string.Empty;
        public long CpuUs { get; set; }
        public long ContextSwitches { get; set; }
        public long ReadyCount { get; set; }
        public long ReadyLatencyUs { get; set; }
        public long? MaxReadyLatencyUs { get; set; }
        public long QuantumEndSwitches { get; set; }
        public long PreemptedSwitches { get; set; }
        public Dictionary<int, long> CoreCpuUs { get; } = new();
    }

    private readonly record struct RawThreadKey(int Pid, int Tid);
}
