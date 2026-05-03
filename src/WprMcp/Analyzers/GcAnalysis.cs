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
        var rows = new List<GcEventRow>();
        long totalGcUs = 0, totalPauseUs = 0;
        int gen0 = 0, gen1 = 0, gen2 = 0;

        ClrEventWalker.Walk(trace, clr =>
        {
            clr.GCStart += data =>
            {
                var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
                if (pid is { } p && data.ProcessID != p) return;
                pendingStarts[(data.ProcessID, data.Count)] =
                    (nowUs, data.Depth, data.Reason.ToString());
            };

            clr.GCStop += data =>
            {
                var endUsLocal = (long)(data.TimeStampRelativeMSec * 1000);
                if (pid is { } p && data.ProcessID != p) return;
                if (!pendingStarts.Remove((data.ProcessID, data.Count), out var s)) return;

                var dur = endUsLocal - s.startUs;
                if (startUs is { } sw && s.startUs < sw) return;
                if (endUs is { } ew && endUsLocal > ew) return;

                totalGcUs += dur;
                if (s.generation == 0) gen0++;
                else if (s.generation == 1) gen1++;
                else gen2++;

                rows.Add(new GcEventRow(
                    StartUs: s.startUs,
                    DurationUs: dur,
                    Generation: s.generation,
                    Reason: s.reason,
                    Pid: data.ProcessID,
                    PauseUs: null)); // pause filled in by SuspendEE pass below
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
                if (startUs is { } sw && startUsLocal < sw) return;
                if (endUs is { } ew && endUsLocal > ew) return;

                totalPauseUs += pauseUs;

                // Attribute the pause to the most-recent GC for this process whose interval
                // covers the suspend/restart window.  This is best-effort: if a GC and a
                // suspend happen back-to-back without a covering GCStart we just record the
                // pause as a free-standing entry.
                var matched = false;
                for (int i = rows.Count - 1; i >= 0 && !matched; i--)
                {
                    var r = rows[i];
                    if (r.Pid != data.ProcessID) continue;
                    if (r.StartUs > endUsLocal) continue;
                    if (r.StartUs + r.DurationUs < startUsLocal) break; // GC ended before this pause
                    rows[i] = r with { PauseUs = (r.PauseUs ?? 0) + pauseUs };
                    matched = true;
                }
                if (!matched)
                {
                    rows.Add(new GcEventRow(
                        StartUs: startUsLocal,
                        DurationUs: pauseUs,
                        Generation: -1,
                        Reason: "(pause without enclosing GCStart/GCStop)",
                        Pid: data.ProcessID,
                        PauseUs: pauseUs));
                }
            };
        });

        rows.Sort((a, b) => a.StartUs.CompareTo(b.StartUs));

        var warnings = new List<string>();
        if (rows.Count == 0)
        {
            warnings.Add(
                "No CLR GC events matched. Either the trace lacks the .NET runtime ETW " +
                "provider (Microsoft-Windows-DotNETRuntime, GC keyword), or no GC happened in " +
                "the window for the given PID. WPR profiles need an explicit <EventCollectorId> " +
                "for the runtime provider to capture GC events.");
        }

        return new GcAnalysisResponse(
            Pid: pid,
            TotalGcCount: rows.Count,
            Gen0Count: gen0,
            Gen1Count: gen1,
            Gen2Count: gen2,
            TotalGcUs: totalGcUs,
            TotalPauseUs: totalPauseUs,
            Events: rows,
            Warnings: warnings);
    }
}
