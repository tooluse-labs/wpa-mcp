using System.Diagnostics;       // ThreadWaitReason (the property type on CSwitchTraceData
                                  // is the BCL enum, NOT a TraceEvent-defined one)
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Per-thread blocked-time analysis from CSwitch events.
//
// Algorithm (simplified port of PerfView's ThreadTimeStackComputer in
// src/TraceEvent/Computers/ThreadTimeStackComputer.cs):
//
//   For each process/thread T, maintain:
//     lastSwitchInTime[T]  — when T last started running on a CPU
//     lastSwitchOutTime[T] — when T last stopped running
//     lastWaitReason[T]    — wait reason captured when T switched out
//
//   On each CSwitch event:
//     oldTid (switching OUT):
//       cpuTime[oldTid] += now - lastSwitchInTime[oldTid]
//       lastSwitchOutTime[oldTid] = now
//       lastWaitReason[oldTid] = OldThreadWaitReason
//
//     newTid (switching IN):
//       blocked[newTid] += now - lastSwitchOutTime[newTid]
//       lastSwitchInTime[newTid] = now
//
// Threads without a prior switch-out are skipped on their first switch-in
// (we have no anchor time). This under-counts blocked-from-trace-start time
// but avoids wild over-counting for threads that existed before the trace.
//
// We do NOT build a stack source — that's PerfView's expensive part. We just
// track aggregates and the dominant wait reasons. This is enough to answer
// "which threads spent wall time blocked, and on what?" — the question that
// the dllhost-53x case in the analysis log left unprovable.
public static class WaitAnalysis
{
    // KWAIT_REASON value → name. CSwitchTraceData.OldThreadWaitReason is typed as
    // System.Diagnostics.ThreadWaitReason (BCL enum), which only names values 0..13. The
    // Windows kernel KWAIT_REASON range goes through ~41 — values past 13 fall through
    // .NET's enum boxing as raw integers (e.g. "22", "37"). This table mirrors ntddk.h's
    // KWAIT_REASON (Windows 10/11) so we can render the canonical kernel name regardless
    // of what the BCL knows. Out-of-range values fall through to "Wait_<n>".
    //
    // Note: a few BCL names diverge from the kernel canonical (e.g. BCL "SystemAllocation"
    // = kernel "PoolAllocation" at index 3). We use the kernel names — they are what
    // PerfView and Microsoft's own kernel-debugging docs use, so they cross-reference more
    // cleanly with EDR / minifilter literature.
    private static readonly string[] WaitReasonNames =
    {
        /* 0  */ "Executive",
        /* 1  */ "FreePage",
        /* 2  */ "PageIn",
        /* 3  */ "PoolAllocation",
        /* 4  */ "DelayExecution",
        /* 5  */ "Suspended",
        /* 6  */ "UserRequest",
        /* 7  */ "WrExecutive",
        /* 8  */ "WrFreePage",
        /* 9  */ "WrPageIn",
        /* 10 */ "WrPoolAllocation",
        /* 11 */ "WrDelayExecution",
        /* 12 */ "WrSuspended",
        /* 13 */ "WrUserRequest",
        /* 14 */ "WrSpare0",
        /* 15 */ "WrQueue",
        /* 16 */ "WrLpcReceive",
        /* 17 */ "WrLpcReply",
        /* 18 */ "WrVirtualMemory",
        /* 19 */ "WrPageOut",
        /* 20 */ "WrRendezvous",
        /* 21 */ "WrKeyedEvent",
        /* 22 */ "WrTerminated",
        /* 23 */ "WrProcessInSwap",
        /* 24 */ "WrCpuRateControl",
        /* 25 */ "WrCalloutStack",
        /* 26 */ "WrKernel",
        /* 27 */ "WrResource",
        /* 28 */ "WrPushLock",
        /* 29 */ "WrMutex",
        /* 30 */ "WrQuantumEnd",
        /* 31 */ "WrDispatchInt",
        /* 32 */ "WrPreempted",
        /* 33 */ "WrYieldExecution",
        /* 34 */ "WrFastMutex",
        /* 35 */ "WrGuardedMutex",
        /* 36 */ "WrRundown",
        /* 37 */ "WrAlertByThreadId",
        /* 38 */ "WrDeferredPreempt",
        /* 39 */ "WrPhysicalFault",
        /* 40 */ "WrIoRing",
        /* 41 */ "WrMdlCache",
    };

    public static string WaitReasonName(ThreadWaitReason reason)
    {
        var idx = (int)reason;
        return (uint)idx < (uint)WaitReasonNames.Length
            ? WaitReasonNames[idx]
            : $"Wait_{idx}";
    }

    public static WaitAnalysisResponse Analyze(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs)
    {
        var accumulator = new WaitAnalysisAccumulator(top, pid, startUs, endUs);

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ThreadCSwitch += data =>
                accumulator.Process(new WaitAnalysisSwitchEvent(
                    OldProcessId: data.OldProcessID,
                    OldProcessName: data.OldProcessName ?? string.Empty,
                    OldThreadId: data.OldThreadID,
                    OldThreadWaitReason: data.OldThreadWaitReason,
                    NewProcessId: data.NewProcessID,
                    NewProcessName: data.NewProcessName ?? string.Empty,
                    NewThreadId: data.NewThreadID,
                    TimeStampRelativeMSec: data.TimeStampRelativeMSec));
        });

        return accumulator.BuildResponse();
    }
}

internal readonly record struct WaitAnalysisSwitchEvent(
    int OldProcessId,
    string OldProcessName,
    int OldThreadId,
    ThreadWaitReason OldThreadWaitReason,
    int NewProcessId,
    string NewProcessName,
    int NewThreadId,
    double TimeStampRelativeMSec);

internal sealed class WaitAnalysisAccumulator
{
    private readonly int _top;
    private readonly int? _pid;
    private readonly long? _startUs;
    private readonly long? _endUs;

    private readonly Dictionary<ThreadKey, long> _threadCpu = new();
    private readonly Dictionary<ThreadKey, long> _threadBlocked = new();
    private readonly Dictionary<ThreadKey, long> _threadCSwitchCount = new();
    private readonly Dictionary<ThreadKey, Dictionary<string, (long blocked, long count)>> _threadWaitReasons = new();
    private readonly Dictionary<ThreadKey, double> _lastSwitchOutTime = new();
    private readonly Dictionary<ThreadKey, double> _lastSwitchInTime = new();
    private readonly Dictionary<ThreadKey, string> _lastWaitReason = new();
    private readonly Dictionary<ThreadKey, string> _processNames = new();

    private long _totalCSwitches;
    private long _traceCSwitches;

    public WaitAnalysisAccumulator(int top, int? pid, long? startUs, long? endUs)
    {
        _top = top;
        _pid = pid;
        _startUs = startUs;
        _endUs = endUs;
    }

    public void Process(WaitAnalysisSwitchEvent data)
    {
        _traceCSwitches++;
        var tsUs = (long)(data.TimeStampRelativeMSec * 1000);
        var inWindow =
            (!_startUs.HasValue || tsUs >= _startUs.Value) &&
            (!_endUs.HasValue || tsUs < _endUs.Value);

        if (inWindow) _totalCSwitches++;

        var nowMs = data.TimeStampRelativeMSec;

        // --- Thread switching OUT ---
        if (TryMakeKey(data.OldProcessId, data.OldThreadId, out var oldKey))
        {
            if (_lastSwitchInTime.TryGetValue(oldKey, out var inMs))
            {
                var cpuUs = IntersectUs(inMs, nowMs);
                if (cpuUs > 0)
                    _threadCpu[oldKey] = _threadCpu.GetValueOrDefault(oldKey) + cpuUs;
            }
            _lastSwitchOutTime[oldKey] = nowMs;
            _lastWaitReason[oldKey] = WaitAnalysis.WaitReasonName(data.OldThreadWaitReason);

            _processNames[oldKey] = data.OldProcessName;
            if (inWindow)
                _threadCSwitchCount[oldKey] = _threadCSwitchCount.GetValueOrDefault(oldKey) + 1;
        }

        // --- Thread switching IN ---
        if (TryMakeKey(data.NewProcessId, data.NewThreadId, out var newKey))
        {
            if (_lastSwitchOutTime.TryGetValue(newKey, out var outMs))
            {
                var blockedUs = IntersectUs(outMs, nowMs);
                if (blockedUs > 0)
                {
                    _threadBlocked[newKey] = _threadBlocked.GetValueOrDefault(newKey) + blockedUs;
                    var reason = _lastWaitReason.GetValueOrDefault(newKey, "Unknown");
                    if (!_threadWaitReasons.TryGetValue(newKey, out var reasons))
                        _threadWaitReasons[newKey] = reasons = new Dictionary<string, (long, long)>();
                    var prev = reasons.GetValueOrDefault(reason);
                    reasons[reason] = (prev.blocked + blockedUs, prev.count + 1);
                }
            }
            _lastSwitchInTime[newKey] = nowMs;

            _processNames[newKey] = data.NewProcessName;
            if (inWindow)
                _threadCSwitchCount[newKey] = _threadCSwitchCount.GetValueOrDefault(newKey) + 1;
        }
    }

    public WaitAnalysisResponse BuildResponse()
    {
        // Build candidate set, then filter+sort.
        var allThreads = new HashSet<ThreadKey>(_threadBlocked.Keys);
        allThreads.UnionWith(_threadCpu.Keys);

        var candidates = allThreads
            .Select(thread =>
            {
                var cpu = _threadCpu.GetValueOrDefault(thread);
                var blocked = _threadBlocked.GetValueOrDefault(thread);
                double? ratio = cpu > 0 ? (double)blocked / cpu : (double?)null;
                var reasons = _threadWaitReasons.GetValueOrDefault(thread)?
                    .OrderByDescending(r => r.Value.blocked)
                    .Take(5)
                    .Select(r => new WaitReasonBucket(r.Key, r.Value.blocked, r.Value.count))
                    .ToList()
                    ?? new List<WaitReasonBucket>();
                return new WaitAnalysisRow(
                    Pid: thread.Pid,
                    ProcessName: _processNames.GetValueOrDefault(thread, string.Empty),
                    Tid: thread.Tid,
                    CpuUs: cpu,
                    BlockedUs: blocked,
                    WaitRatio: ratio,
                    ContextSwitches: _threadCSwitchCount.GetValueOrDefault(thread),
                    TopWaitReasons: reasons);
            });

        if (_pid is { } p)
            candidates = candidates.Where(r => r.Pid == p);

        var rows = candidates
            .OrderByDescending(r => r.BlockedUs)
            .Take(_top)
            .ToList();

        var warnings = new List<string>();
        if (_traceCSwitches == 0)
        {
            warnings.Add(
                "No CSwitch events found. The capture profile must include the CSwitch keyword. " +
                "Default WPR 'CPU' / 'CPU.light' profiles include it; some custom .wprp files may not.");
        }
        else if (_totalCSwitches == 0 && rows.Count == 0)
        {
            warnings.Add("CSwitch events were present in the trace, but none landed inside the requested time window.");
        }

        return new WaitAnalysisResponse(rows, _totalCSwitches, warnings);
    }

    private static bool TryMakeKey(int pid, int tid, out ThreadKey key)
    {
        key = new ThreadKey(pid, tid);
        return pid > 0 && tid != 0;
    }

    private long IntersectUs(double startMs, double endMs)
    {
        var startUs = (long)(startMs * 1000);
        var endUs = (long)(endMs * 1000);
        if (endUs <= startUs) return 0;

        var clippedStart = _startUs.HasValue ? Math.Max(startUs, _startUs.Value) : startUs;
        var clippedEnd = _endUs.HasValue ? Math.Min(endUs, _endUs.Value) : endUs;
        return Math.Max(0, clippedEnd - clippedStart);
    }

    private readonly record struct ThreadKey(int Pid, int Tid);
}
