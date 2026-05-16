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
//   For each thread T, maintain:
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
        var threadCpu = new Dictionary<int, long>();
        var threadBlocked = new Dictionary<int, long>();
        var threadCSwitchCount = new Dictionary<int, long>();
        var threadWaitReasons = new Dictionary<int, Dictionary<string, (long blocked, long count)>>();
        var lastSwitchOutTime = new Dictionary<int, double>();
        var lastSwitchInTime = new Dictionary<int, double>();
        var lastWaitReason = new Dictionary<int, string>();
        var threadProcess = new Dictionary<int, (int pid, string name)>();

        long totalCSwitches = 0;
        long traceCSwitches = 0;

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ThreadCSwitch += data =>
        {
            traceCSwitches++;
            var tsUs = (long)(data.TimeStampRelativeMSec * 1000);
            var inWindow =
                (!startUs.HasValue || tsUs >= startUs.Value) &&
                (!endUs.HasValue || tsUs < endUs.Value);

            if (inWindow) totalCSwitches++;

            var nowMs = data.TimeStampRelativeMSec;
            var oldTid = data.OldThreadID;
            var newTid = data.NewThreadID;

            // --- Thread switching OUT ---
            if (oldTid != 0)
            {
                if (lastSwitchInTime.TryGetValue(oldTid, out var inMs))
                {
                    var cpuMs = nowMs - inMs;
                    if (cpuMs > 0 && inWindow)
                        threadCpu[oldTid] = threadCpu.GetValueOrDefault(oldTid) + (long)(cpuMs * 1000);
                }
                lastSwitchOutTime[oldTid] = nowMs;
                lastWaitReason[oldTid] = WaitReasonName(data.OldThreadWaitReason);

                // Record process membership (the kernel reports it on every CSwitch).
                if (data.OldProcessID > 0)
                    threadProcess[oldTid] = (data.OldProcessID, data.OldProcessName ?? string.Empty);
                if (inWindow)
                    threadCSwitchCount[oldTid] = threadCSwitchCount.GetValueOrDefault(oldTid) + 1;
            }

            // --- Thread switching IN ---
            if (newTid != 0)
            {
                if (lastSwitchOutTime.TryGetValue(newTid, out var outMs))
                {
                    var blockedMs = nowMs - outMs;
                    if (blockedMs > 0 && inWindow)
                    {
                        var blockedUs = (long)(blockedMs * 1000);
                        threadBlocked[newTid] = threadBlocked.GetValueOrDefault(newTid) + blockedUs;
                        var reason = lastWaitReason.GetValueOrDefault(newTid, "Unknown");
                        if (!threadWaitReasons.TryGetValue(newTid, out var reasons))
                            threadWaitReasons[newTid] = reasons = new Dictionary<string, (long, long)>();
                        var prev = reasons.GetValueOrDefault(reason);
                        reasons[reason] = (prev.blocked + blockedUs, prev.count + 1);
                    }
                }
                lastSwitchInTime[newTid] = nowMs;

                if (data.NewProcessID > 0)
                    threadProcess[newTid] = (data.NewProcessID, data.NewProcessName ?? string.Empty);
                if (inWindow)
                    threadCSwitchCount[newTid] = threadCSwitchCount.GetValueOrDefault(newTid) + 1;
            }
        };
        });

        // Build candidate set, then filter+sort.
        var allTids = new HashSet<int>(threadBlocked.Keys);
        allTids.UnionWith(threadCpu.Keys);

        var candidates = allTids
            .Select(tid =>
            {
                var (procPid, procName) = threadProcess.GetValueOrDefault(tid, (-1, string.Empty));
                var cpu = threadCpu.GetValueOrDefault(tid);
                var blocked = threadBlocked.GetValueOrDefault(tid);
                double? ratio = cpu > 0 ? (double)blocked / cpu : (double?)null;
                var reasons = threadWaitReasons.GetValueOrDefault(tid)?
                    .OrderByDescending(r => r.Value.blocked)
                    .Take(5)
                    .Select(r => new WaitReasonBucket(r.Key, r.Value.blocked, r.Value.count))
                    .ToList()
                    ?? new List<WaitReasonBucket>();
                return new WaitAnalysisRow(
                    Pid: procPid,
                    ProcessName: procName,
                    Tid: tid,
                    CpuUs: cpu,
                    BlockedUs: blocked,
                    WaitRatio: ratio,
                    ContextSwitches: threadCSwitchCount.GetValueOrDefault(tid),
                    TopWaitReasons: reasons);
            });

        if (pid is { } p)
            candidates = candidates.Where(r => r.Pid == p);

        var rows = candidates
            .OrderByDescending(r => r.BlockedUs)
            .Take(top)
            .ToList();

        var warnings = new List<string>();
        if (traceCSwitches == 0)
        {
            warnings.Add(
                "No CSwitch events found. The capture profile must include the CSwitch keyword. " +
                "Default WPR 'CPU' / 'CPU.light' profiles include it; some custom .wprp files may not.");
        }
        else if (totalCSwitches == 0)
        {
            warnings.Add("CSwitch events were present in the trace, but none landed inside the requested time window.");
        }

        return new WaitAnalysisResponse(rows, totalCSwitches, warnings);
    }
}
