using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by blocked microseconds — the canonical "what stack is this thread
// stuck inside while *not* on CPU" view. Complements WaitAnalysis (per-thread aggregate
// blocked time + dominant wait reason): wait_analysis tells you *which thread* and *which
// kernel wait reason*, blocked_top_stacks tells you *which call chain* across all threads.
//
// Algorithm — simplified port of PerfView's ThreadTimeStackComputer
// (src/TraceEvent/Computers/ThreadTimeStackComputer.cs), BlockedTime mode:
//
//   For each thread T, track lastSwitchOutTime[T].
//   On every ThreadCSwitch event:
//     newTid (switching IN):
//       blockedMs = now - lastSwitchOutTime[newTid]   (skip if no anchor)
//       sample.StackIndex = data.CallStackIndex()     (resume stack of newTid)
//       sample.Metric     = blockedMs * 1000          (microseconds)
//       rawSource.AddSample(sample)
//     oldTid (switching OUT):
//       lastSwitchOutTime[oldTid] = now
//
// Why the resume stack? When CSwitch fires, the kernel walks the NEW thread's stack —
// which is the resume context, i.e., where the thread will return to after wait. For
// almost every blocking call, the resume point IS the wait point (the syscall returns
// from the kernel's wait primitive into the user-mode caller that asked to wait). PerfView
// uses this convention; it's accurate enough that ThreadTimeStackComputer ships it as the
// default for every "blocked time" analysis they do.
//
// PerfView-parity invariants are enforced via StackSourceTopN — see that file for the full
// list. The relevant ones here: synthetic ?!? root for no-stack samples, LookupWarmSymbols
// before normalization, raw-frame symbol stats, module!? folding.
public static class BlockedTimeStackAnalysis
{
    public static WaitTopStacksResponse TopBlockedStacks(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        int whenBuckets = 0)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, whenBuckets);
        var req = new StackAnalysisRequest(pid, startUs, endUs, symbolLog, when);
        var ctx = BuildNormalized(trace, req);

        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = ctx.Normalized };
        var totalMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        // Rank by ExclusiveMetric (sum of blocked μs ending at this frame). ExclusiveCount
        // would equal "number of CSwitch resumes hitting this frame" — meaningless for
        // blocked-time ranking when individual waits range from 1 µs to seconds.
        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveMetric)
            .Take(top)
            .Select(n => new WaitStackRow(
                Function: n.Name,
                ExclusiveBlockedUs: (long)n.ExclusiveMetric,
                InclusiveBlockedUs: (long)n.InclusiveMetric,
                ExclusivePct: 100.0 * n.ExclusiveMetric / totalMetric,
                InclusivePct: 100.0 * n.InclusiveMetric / totalMetric,
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalBlockedUs, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalBlockedUs, n.InclusiveMetric)))
            .ToList();

        return new WaitTopStacksResponse(
            Rows: rows,
            TotalBlockedUs: ctx.TotalBlockedUs,
            SampleCount: ctx.SampleCount,
            Stats: ctx.Stats,
            Warnings: ctx.Warnings,
            When: when.Build());
    }

    public static CallerCalleeResponse CallerCallee(
        TraceLog trace,
        string focusFunction,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, 0);
        var req = new StackAnalysisRequest(pid, startUs, endUs, symbolLog, when);
        var ctx = BuildNormalized(trace, req);
        return StackSourceTopN.ComputeCallerCallee(
            ctx.Normalized, focusFunction, top, metricName: "blockedUs", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalBlockedUs,
        long TotalBlockedUs,
        long SampleCount,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        var lastSwitchOutTime = new Dictionary<int, double>();
        long traceTotalBlockedUs = 0;
        long totalBlockedUs = 0;
        long sampleCount = 0;
        long totalCSwitches = 0;

        // Single pass: track per-thread blocked intervals, tally trace-total unconditionally
        // (so InclusivePctOfTrace is accurate when filtered), and only feed samples to the
        // stack source when the event passes the pid/window filter. The state machine MUST
        // run on every event regardless of filter — an out-of-window switch-out is what
        // anchors a later in-window switch-in.
        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ThreadCSwitch += data =>
            {
                totalCSwitches++;
                var nowMs = data.TimeStampRelativeMSec;
                var nowUs = (long)(nowMs * 1000);
                var oldTid = data.OldThreadID;
                var newTid = data.NewThreadID;

                if (newTid != 0 && lastSwitchOutTime.TryGetValue(newTid, out var outMs))
                {
                    var blockedMs = nowMs - outMs;
                    if (blockedMs > 0)
                    {
                        var blockedUs = (long)(blockedMs * 1000);
                        traceTotalBlockedUs += blockedUs;

                        var inWindow =
                            (!req.StartUs.HasValue || nowUs >= req.StartUs.Value) &&
                            (!req.EndUs.HasValue || nowUs < req.EndUs.Value);
                        var inPid = !req.Pid.HasValue || data.NewProcessID == req.Pid.Value;
                        if (inWindow && inPid)
                        {
                            totalBlockedUs += blockedUs;
                            sampleCount++;
                            raw.AddSample(data.CallStackIndex(), data, blockedUs);
                            // CSwitch is the END of a wait that may span buckets — charge
                            // the full interval to the bucket where the wait ended (resume
                            // bucket). Matches PerfView's sample-time-based "When" semantics.
                            req.When.Add(nowUs, blockedUs);
                        }
                    }
                }

                if (oldTid != 0) lastSwitchOutTime[oldTid] = nowMs;
            };
        });
        raw.Source.DoneAddingSamples();

        raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var warnings = new List<string>();
        if (totalCSwitches == 0)
        {
            warnings.Add(
                "No CSwitch events found. The capture profile must include the CSwitch keyword. " +
                "Default WPR 'CPU' / 'CPU.light' profiles include it; some custom .wprp files may not.");
        }
        else if (sampleCount == 0)
        {
            warnings.Add(
                "CSwitch events present but no blocked-time samples landed in the requested filter. " +
                "Either the pid/window picked a thread set with no waits, or every thread's first " +
                "switch-in inside the window had no anchor switch-out (under-counted by design).");
        }
        if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalBlockedUs, totalBlockedUs, sampleCount, warnings);
    }
}
