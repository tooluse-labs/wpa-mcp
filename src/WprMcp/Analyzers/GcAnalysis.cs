using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// .NET CLR Garbage Collection analysis — list of GCs and per-GC "stop the world" pauses.
// PerfView equivalent: 'GCStats' / 'GC HeapAlloc Stacks'.
//
// Two related-but-distinct events to track:
//
//   GCStart / GCEnd: bracket a single GC operation.  Each carries Count (a sequence ID),
//     Depth (the generation: 0/1/2 == Gen0/Gen1/Gen2 (LOH or POH for higher)), and Reason
//     (Induced / AllocSmall / AllocLarge / etc.).  The interval is the WALL TIME of the GC,
//     not necessarily the pause time — for background / concurrent GCs, mutator threads keep
//     running for most of it.
//
//   GCSuspendEEStart / GCRestartEEStop: bracket the "stop the world" suspension that all
//     CLR GCs do at least briefly.  This interval IS the pause that user code experiences.
//     For a workstation Gen0 GC the entire GC happens in this window; for a server background
//     Gen2 GC the suspension is only the initial mark phase.
//
// We match GCStart→GCEnd by ProcessID + Count to get per-GC metadata (generation, reason)
// and SuspendEEStart→RestartEEStop to compute pause time.  Combining the two: for each GC,
// report wall duration AND pause duration.  Pauses without an enclosing GC are reported
// separately (rare, but possible during early-startup / late-shutdown windows).
public static class GcAnalysis
{
    public static GcAnalysisResponse Analyze(TraceLog trace, int? pid, long? startUs, long? endUs)
    {
        var pendingStarts = new Dictionary<(int pid, int count), (long startUs, int generation, string reason)>();
        var pendingSuspends = new Dictionary<int, long>();
        // Pause time accumulated INSIDE an in-flight GC (between GCStart and GCStop) for a
        // PID — Suspend/Restart fires before GCStop, so the GC's row doesn't exist yet at
        // attribution time.  GCStop reads-and-clears this entry to populate PauseUs.
        var pauseAccumByPid = new Dictionary<int, long>();
        // Pauses that fired with NO in-flight GC for the PID — emitted as standalone rows.
        var orphanPauses = new List<GcEventRow>();
        var rows = new List<GcEventRow>();
        long totalGcUs = 0, totalPauseUs = 0;
        int gcCount = 0, gen0 = 0, gen1 = 0, gen2 = 0;

        ClrEventWalker.Walk(trace, clr =>
        {
            clr.GCStart += data =>
            {
                var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
                if (pid is { } p && data.ProcessID != p) return;
                pendingStarts[(data.ProcessID, data.Count)] =
                    (nowUs, data.Depth, data.Reason.ToString());
                // Reset any orphan pause carry-over for this PID — pauses now belong to this GC.
                pauseAccumByPid[data.ProcessID] = 0;
            };

            clr.GCStop += data =>
            {
                var endUsLocal = (long)(data.TimeStampRelativeMSec * 1000);
                if (pid is { } p && data.ProcessID != p) return;
                if (!pendingStarts.Remove((data.ProcessID, data.Count), out var s)) return;

                // Always clear the accumulator — this GC is finished regardless of window.
                // Otherwise stale pauses leak into the next GC's bucket (or get clobbered
                // by the next GCStart resetting it to 0, silently dropped).
                pauseAccumByPid.Remove(data.ProcessID, out var pauseSum);

                if (startUs is { } sw && s.startUs < sw) return;
                if (endUs is { } ew && endUsLocal > ew) return;

                var dur = endUsLocal - s.startUs;
                totalGcUs += dur;
                if (pauseSum > 0) totalPauseUs += pauseSum;
                gcCount++;
                if (s.generation == 0) gen0++;
                else if (s.generation == 1) gen1++;
                else gen2++;

                rows.Add(new GcEventRow(
                    StartUs: s.startUs,
                    DurationUs: dur,
                    Generation: s.generation,
                    Reason: s.reason,
                    Pid: data.ProcessID,
                    PauseUs: pauseSum > 0 ? pauseSum : null));
            };

            clr.GCSuspendEEStart += data =>
            {
                var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
                if (pid is { } p && data.ProcessID != p) return;
                pendingSuspends[data.ProcessID] = nowUs;
            };

            clr.GCRestartEEStop += data =>
            {
                var endUsLocal = (long)(data.TimeStampRelativeMSec * 1000);
                if (pid is { } p && data.ProcessID != p) return;
                if (!pendingSuspends.Remove(data.ProcessID, out var startUsLocal)) return;

                var pauseUs = endUsLocal - startUsLocal;

                if (pauseAccumByPid.ContainsKey(data.ProcessID))
                {
                    // GC in flight — accumulate; GCStop applies the window and adds to totalPauseUs.
                    pauseAccumByPid[data.ProcessID] += pauseUs;
                    return;
                }

                // No enclosing GC — emit as standalone, window-gated to match GCStop's behavior.
                if (startUs is { } sw && startUsLocal < sw) return;
                if (endUs is { } ew && endUsLocal > ew) return;

                totalPauseUs += pauseUs;
                orphanPauses.Add(new GcEventRow(
                    StartUs: startUsLocal,
                    DurationUs: pauseUs,
                    Generation: -1,
                    Reason: "(pause without enclosing GCStart/GCStop)",
                    Pid: data.ProcessID,
                    PauseUs: pauseUs));
            };
        });

        rows.AddRange(orphanPauses);

        rows.Sort((a, b) => a.StartUs.CompareTo(b.StartUs));

        var warnings = new List<string>();
        if (rows.Count == 0)
            warnings.Add(WarningBuilder.MissingClrKeyword("GC", "GC", "or no GC occurred in the filter window"));

        return new GcAnalysisResponse(
            Pid: pid,
            TotalGcCount: gcCount,
            Gen0Count: gen0,
            Gen1Count: gen1,
            Gen2Count: gen2,
            TotalGcUs: totalGcUs,
            TotalPauseUs: totalPauseUs,
            Events: rows,
            Warnings: warnings);
    }
}
