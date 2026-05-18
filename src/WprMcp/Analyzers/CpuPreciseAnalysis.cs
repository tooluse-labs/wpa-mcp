using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// CPU Usage (Precise)-style summary from CSwitch + ReadyThread events.
//
// CPU sampling answers "where was the instruction pointer when the sampler fired".
// This analyzer answers scheduler questions sampling cannot: exact run intervals from
// context switches, which core the thread ran on, and how long a readied thread waited
// before it actually got CPU time.
public static class CpuPreciseAnalysis
{
    public static CpuPreciseResponse Analyze(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs)
    {
        var accumulator = new CpuPreciseAccumulator(
            top,
            pid,
            startUs,
            endUs,
            traceEndUs: (long)trace.SessionDuration.TotalMicroseconds);

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ThreadCSwitch += data =>
                accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
                    OldProcessId: data.OldProcessID,
                    OldProcessName: data.OldProcessName ?? string.Empty,
                    OldThreadId: data.OldThreadID,
                    OldThreadWaitReason: data.OldThreadWaitReason,
                    NewProcessId: data.NewProcessID,
                    NewProcessName: data.NewProcessName ?? string.Empty,
                    NewThreadId: data.NewThreadID,
                    ProcessorNumber: data.ProcessorNumber,
                    TimeStampRelativeMSec: data.TimeStampRelativeMSec));

            kernel.DispatcherReadyThread += data =>
                accumulator.ProcessReady(new CpuPreciseReadyEvent(
                    AwakenedProcessId: data.AwakenedProcessID,
                    AwakenedThreadId: data.AwakenedThreadID,
                    TimeStampRelativeMSec: data.TimeStampRelativeMSec));
        });

        return accumulator.BuildResponse();
    }
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

internal sealed class CpuPreciseAccumulator
{
    private readonly int _top;
    private readonly int? _pid;
    private readonly long? _startUs;
    private readonly long? _endUs;
    private readonly long _traceEndUs;

    private readonly Dictionary<ThreadKey, ThreadStats> _threads = new();
    // At any instant each logical processor has at most one running thread. Tracking
    // open run intervals per core keeps CPU accounting bounded to scheduler semantics.
    private readonly Dictionary<int, RunningThread> _runningByCore = new();
    private readonly Dictionary<ThreadKey, long> _pendingReadyUs = new();
    private readonly HashSet<ThreadKey> _seenThreads = new();
    private readonly HashSet<int> _seenSwitchOutCores = new();

    private long _traceCSwitches;
    private long _windowCSwitches;
    private long _traceReadyEvents;
    private long _windowReadyEvents;
    private long _skippedUnmatchedSwitchOuts;
    private long _droppedStaleCoreIntervals;
    private bool _flushed;

    public CpuPreciseAccumulator(int top, int? pid, long? startUs, long? endUs, long? traceEndUs = null)
    {
        _top = top;
        _pid = pid;
        _startUs = startUs;
        _endUs = endUs;
        _traceEndUs = traceEndUs ?? endUs ?? 0;
    }

    public void ProcessReady(CpuPreciseReadyEvent data)
    {
        _traceReadyEvents++;
        var nowUs = ToUs(data.TimeStampRelativeMSec);
        if (InWindow(nowUs) && MatchesPid(data.AwakenedProcessId))
            _windowReadyEvents++;

        if (!TryMakeKey(data.AwakenedProcessId, data.AwakenedThreadId, out var key))
            return;
        _seenThreads.Add(key);

        // Keep the earliest unconsumed ready timestamp so repeated ReadyThread events do
        // not hide the full ready-to-run delay before the next CSwitch-in.
        if (!_pendingReadyUs.ContainsKey(key))
            _pendingReadyUs[key] = nowUs;
    }

    public void ProcessCSwitch(CpuPreciseSwitchEvent data)
    {
        _traceCSwitches++;
        var nowUs = ToUs(data.TimeStampRelativeMSec);
        if (InWindow(nowUs) && (MatchesPid(data.OldProcessId) || MatchesPid(data.NewProcessId)))
            _windowCSwitches++;

        var core = data.ProcessorNumber;
        var canSeedFromWindowStart = _seenSwitchOutCores.Add(core);
        if (TryMakeKey(data.OldProcessId, data.OldThreadId, out var oldKey))
        {
            var firstObservedThread = _seenThreads.Add(oldKey);
            var oldStats = GetStats(oldKey, data.OldProcessName);
            var shouldAttributeCpu = false;
            var switchInUs = nowUs;

            if (_runningByCore.TryGetValue(core, out var running))
            {
                if (running.Key == oldKey)
                {
                    switchInUs = running.SwitchInUs;
                    shouldAttributeCpu = true;
                }
                else
                {
                    _droppedStaleCoreIntervals++;
                }
            }
            else if (canSeedFromWindowStart && firstObservedThread)
            {
                switchInUs = WindowStartUs;
                shouldAttributeCpu = true;
            }
            else
            {
                _skippedUnmatchedSwitchOuts++;
            }

            _runningByCore.Remove(core);

            if (shouldAttributeCpu)
            {
                var cpuUs = IntersectUs(switchInUs, nowUs);
                if (cpuUs > 0 && MatchesPid(oldKey.Pid))
                {
                    AddCpu(oldStats, cpuUs, core);
                }
            }

            if (InWindow(nowUs) && MatchesPid(oldKey.Pid))
            {
                oldStats.ContextSwitches++;
                var reason = WaitAnalysis.WaitReasonName(data.OldThreadWaitReason);
                if (reason == "WrQuantumEnd")
                    oldStats.QuantumEndSwitches++;
                if (reason is "WrPreempted" or "WrDeferredPreempt")
                    oldStats.PreemptedSwitches++;
            }
        }
        else if (_runningByCore.Remove(core))
        {
            _droppedStaleCoreIntervals++;
        }

        if (TryMakeKey(data.NewProcessId, data.NewThreadId, out var newKey))
        {
            _seenThreads.Add(newKey);
            var newStats = GetStats(newKey, data.NewProcessName);
            if (_pendingReadyUs.Remove(newKey, out var readyUs) && InWindow(nowUs) && MatchesPid(newKey.Pid))
            {
                var clippedReadyUs = Math.Max(readyUs, WindowStartUs);
                var latencyUs = Math.Max(0, nowUs - clippedReadyUs);
                newStats.ReadyCount++;
                newStats.ReadyLatencyUs += latencyUs;
                newStats.MaxReadyLatencyUs = Math.Max(newStats.MaxReadyLatencyUs ?? 0, latencyUs);
            }

            _runningByCore[core] = new RunningThread(newKey, nowUs);
            if (InWindow(nowUs) && MatchesPid(newKey.Pid))
                newStats.ContextSwitches++;
        }
    }

    public CpuPreciseResponse BuildResponse()
    {
        FlushOpenRunningIntervals();

        var allRows = _threads
            .Select(kv => ToRow(kv.Key, kv.Value))
            .Where(row => row.CpuUs > 0 || row.ReadyCount > 0 || row.ContextSwitches > 0)
            .OrderByDescending(row => row.CpuUs)
            .ThenByDescending(row => row.ReadyLatencyUs)
            .ToList();
        var rows = allRows.Take(_top).ToList();

        var warnings = new List<string>();
        if (_traceCSwitches == 0)
        {
            warnings.Add(
                "No CSwitch events found. The capture profile must include the CSwitch keyword. " +
                "Default WPR 'CPU' / 'CPU.light' profiles include it; some custom .wprp files may not.");
        }
        else if (_windowCSwitches == 0 && rows.All(row => row.CpuUs == 0 && row.ContextSwitches == 0))
        {
            warnings.Add("CSwitch events were present in the trace, but none matched the requested pid/window filters.");
        }

        if (_traceReadyEvents == 0)
        {
            warnings.Add(
                "No DispatcherReadyThread events found. Ready latency cannot be computed without ReadyThread events.");
        }
        else if (_windowReadyEvents == 0 && rows.All(row => row.ReadyCount == 0))
        {
            warnings.Add("ReadyThread events were present in the trace, but none matched the requested pid/window filters.");
        }
        if (_skippedUnmatchedSwitchOuts > 0)
        {
            warnings.Add(
                $"Skipped {_skippedUnmatchedSwitchOuts:N0} unmatched CSwitch old-thread interval(s) that could not be tied to a prior switch-in or a unique trace-start seed. " +
                "This avoids over-counting CPU time when scheduler state is incomplete or thread IDs are reused.");
        }
        if (_droppedStaleCoreIntervals > 0)
        {
            warnings.Add(
                $"Dropped {_droppedStaleCoreIntervals:N0} stale per-core running interval(s) after later CSwitch data showed a different old thread on that processor. " +
                "This keeps CPU accounting bounded to one running thread per processor.");
        }

        return new CpuPreciseResponse(
            Rows: rows,
            TotalCpuUs: allRows.Sum(row => row.CpuUs),
            TotalContextSwitches: _windowCSwitches,
            TotalReadyCount: allRows.Sum(row => row.ReadyCount),
            TotalReadyLatencyUs: allRows.Sum(row => row.ReadyLatencyUs),
            Warnings: warnings);
    }

    private void FlushOpenRunningIntervals()
    {
        if (_flushed) return;
        _flushed = true;

        var flushEndUs = _endUs ?? _traceEndUs;
        if (flushEndUs <= WindowStartUs) return;

        foreach (var (core, running) in _runningByCore.ToArray())
        {
            var key = running.Key;
            if (!MatchesPid(key.Pid)) continue;

            var cpuUs = IntersectUs(running.SwitchInUs, flushEndUs);
            if (cpuUs <= 0) continue;

            var stats = GetStats(key, processName: string.Empty);
            AddCpu(stats, cpuUs, core);
        }

        _runningByCore.Clear();
    }

    private ThreadStats GetStats(ThreadKey key, string processName)
    {
        if (!_threads.TryGetValue(key, out var stats))
            _threads[key] = stats = new ThreadStats();
        if (!string.IsNullOrEmpty(processName))
            stats.ProcessName = processName;
        return stats;
    }

    private static void AddCpu(ThreadStats stats, long cpuUs, int core)
    {
        stats.CpuUs += cpuUs;
        if (core >= 0)
            stats.CoreCpuUs[core] = stats.CoreCpuUs.GetValueOrDefault(core) + cpuUs;
    }

    private CpuPreciseThreadRow ToRow(ThreadKey key, ThreadStats stats)
    {
        var topCores = stats.CoreCpuUs
            .OrderByDescending(kv => kv.Value)
            .Take(8)
            .Select(kv => new CpuCoreBucket(
                Core: kv.Key,
                CpuUs: kv.Value,
                CpuPct: StackSourceTopN.Pct(stats.CpuUs, kv.Value)))
            .ToList();

        return new CpuPreciseThreadRow(
            Pid: key.Pid,
            ProcessName: stats.ProcessName,
            Tid: key.Tid,
            CpuUs: stats.CpuUs,
            ContextSwitches: stats.ContextSwitches,
            ReadyCount: stats.ReadyCount,
            ReadyLatencyUs: stats.ReadyLatencyUs,
            AvgReadyLatencyUs: stats.ReadyCount > 0 ? (double)stats.ReadyLatencyUs / stats.ReadyCount : null,
            MaxReadyLatencyUs: stats.MaxReadyLatencyUs,
            PrimaryCore: topCores.Count > 0 ? topCores[0].Core : null,
            TopCores: topCores,
            QuantumEndSwitches: stats.QuantumEndSwitches,
            PreemptedSwitches: stats.PreemptedSwitches);
    }

    private bool MatchesPid(int pid) => !_pid.HasValue || pid == _pid.Value;

    private long WindowStartUs => _startUs ?? 0;

    private long WindowEndUs =>
        _endUs ?? (_traceEndUs > 0 ? _traceEndUs : long.MaxValue);

    private bool InWindow(long nowUs) =>
        nowUs >= WindowStartUs && nowUs < WindowEndUs;

    private long IntersectUs(long startUs, long endUs)
    {
        var clippedStart = Math.Max(startUs, WindowStartUs);
        var clippedEnd = Math.Min(endUs, WindowEndUs);
        return Math.Max(0, clippedEnd - clippedStart);
    }

    private static long ToUs(double timeStampRelativeMSec) => (long)(timeStampRelativeMSec * 1000);

    private static bool TryMakeKey(int pid, int tid, out ThreadKey key)
    {
        key = new ThreadKey(pid, tid);
        return pid > 0 && tid != 0;
    }

    private readonly record struct ThreadKey(int Pid, int Tid);

    private readonly record struct RunningThread(ThreadKey Key, long SwitchInUs);

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
}
