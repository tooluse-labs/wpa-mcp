using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Output;

namespace WprMcp.Analyzers;

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
        foreach (var p in trace.Processes)
        {
            if (!includeSystem && (p.ProcessID == 0 || p.ProcessID == 4))
                continue;

            var startUs = (long)(p.StartTimeRelativeMsec * 1000);
            var endUs = (long)(p.EndTimeRelativeMsec * 1000);
            var wallUs = Math.Max(0, endUs - startUs);
            var cpuUs = (long)(p.CPUMSec * 1000);
            // PerfView convention: ratio undefined when CPU time is zero (short-lived processes
            // whose threads were never scheduled during the trace). Null beats +inf in JSON.
            double? ratio = cpuUs > 0 ? (double)wallUs / cpuUs : (double?)null;

            yield return new ProcessRow(
                Pid: p.ProcessID,
                ParentPid: p.ParentID,
                Name: p.Name ?? string.Empty,
                StartUs: startUs,
                EndUs: endUs,
                WallUs: wallUs,
                CpuUs: cpuUs,
                WaitRatio: ratio,
                ImageLoadCount: p.LoadedModules.Count());
        }
    }
}
