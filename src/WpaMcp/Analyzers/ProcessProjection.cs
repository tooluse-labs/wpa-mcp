using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

internal static class ProcessProjection
{
    /// <summary>
    /// Projects each <see cref="TraceProcess"/> in the trace into a <see cref="ProcessRow"/>,
    /// computing WallUs, WaitRatio, and ImageLoadCount inline. PID 0 (Idle) and PID 4 (System)
    /// are skipped by default; they're kernel idle / system threads, not user-meaningful
    /// processes, and including them dominates "top by CPU" rankings unhelpfully.
    /// </summary>
    public static IEnumerable<ProcessRow> Rows(TraceLog trace, bool includeSystem)
    {
        var traceEndUs = (long)trace.SessionDuration.TotalMicroseconds;
        // Snapshot TraceProcess-derived aggregates before the identity event walk. TraceLog's
        // mutable event source can otherwise invalidate lazy process aggregate reads.
        var processes = AnalysisEvents.Enumerate(trace.Processes).Select(process => new
        {
            Pid = process.ProcessID,
            ParentPid = process.ParentID,
            Name = process.Name ?? string.Empty,
            StartUs = (long)(process.StartTimeRelativeMsec * 1000),
            EndUs = (long)(process.EndTimeRelativeMsec * 1000),
            CpuUs = (long)(process.CPUMSec * 1000),
            ImageLoadCount = AnalysisEvents.Enumerate(process.LoadedModules).Count(),
        }).ToArray();
        var identities = TraceIdentityIndex.For(trace);
        // 1 ms epsilon: ETW timestamps round to the timer tick, and TraceProcess.EndTimeRelativeMsec
        // for trace-resident processes is set to the session end with sub-ms slack.
        const long residentEpsilonUs = 1000;

        foreach (var p in AnalysisEvents.Enumerate(processes))
        {
            if (!includeSystem && (p.Pid == 0 || p.Pid == 4))
                continue;

            var startUs = p.StartUs;
            var endUs = p.EndUs;
            var wallUs = Math.Max(0, endUs - startUs);
            var cpuUs = p.CpuUs;
            // PerfView convention: ratio undefined when CPU time is zero (short-lived processes
            // whose threads were never scheduled during the trace). Null beats +inf in JSON.
            double? ratio = cpuUs > 0 ? (double)wallUs / cpuUs : (double?)null;

            var traceResident = startUs == 0 && endUs >= traceEndUs - residentEpsilonUs;
            var key = new ProcessInstanceKey(p.Pid, startUs);
            var lifetime = identities.Processes.FindExact(key)
                .OrderByDescending(candidate => candidate.EndObserved)
                .ThenByDescending(candidate => candidate.EndUs)
                .FirstOrDefault();
            var startObserved = lifetime?.StartObserved == true;
            var endObserved = lifetime?.EndObserved == true;
            var startBoundaryKind = startObserved
                ? "observed"
                : startUs == 0 ? "trace_start" : "inventory_start";
            var endBoundaryKind = endObserved
                ? "observed"
                : lifetime is not null && identities.Processes.Lifetimes.Any(candidate =>
                    candidate.Key.Pid == p.Pid &&
                    candidate.Key.StartUs == lifetime.EndUs)
                    ? "replacement"
                    : (lifetime?.EndUs ?? endUs) >= traceEndUs - residentEpsilonUs
                        ? "trace_end"
                        : "inventory_end";

            yield return new ProcessRow(
                Pid: p.Pid,
                ParentPid: p.ParentPid,
                Name: p.Name,
                StartUs: startUs,
                EndUs: endUs,
                WallUs: wallUs,
                CpuUs: cpuUs,
                WaitRatio: ratio,
                ImageLoadCount: p.ImageLoadCount,
                TraceResident: traceResident,
                ProcessStartObserved: startObserved,
                ProcessEndObserved: endObserved,
                StartBoundaryKind: startBoundaryKind,
                EndBoundaryKind: endBoundaryKind);
        }
    }
}
