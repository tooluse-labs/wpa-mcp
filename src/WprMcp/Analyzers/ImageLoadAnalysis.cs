using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Per-process DLL/image-load sequence with timestamps relative to process start.
//
// Mirrors PerfView's "Image Load Stacks" view (src/TraceEvent/Computers/ImageLoadStackComputer.cs)
// but without the stack source — we just want the ordered list, not a flame graph. Each ImageLoad
// kernel event marks "the kernel mapped this PE into the process's address space"; for "why is
// process X slow to start", the Loader serializes most of these so the *count* and *spread* across
// the process startup window are typically the relevant signals.
//
// True per-DLL load duration requires correlating ImageLoad with the loading thread's blocked-time
// (a job for WaitAnalysis on the LdrpSnapModule region). v1 here just orders the events and reports
// the offset from ProcessStart.
public static class ImageLoadAnalysis
{
    /// <summary>
    /// Bucket ImageLoad events by process for the given PIDs in a single trace pass.
    /// Returns ordered (chronological) lists per requested PID; PIDs not present in the
    /// trace map to an empty list. Use this from composite analyzers (DiagnoseTools) to
    /// avoid running one full event-source pass per candidate.
    /// </summary>
    public static Dictionary<int, List<ImageLoadRow>> ForPids(TraceLog trace, IReadOnlyCollection<int> pids)
    {
        var processStart = pids
            .Distinct()
            .Select(pid => (pid, proc: trace.Processes.FirstOrDefault(p => p.ProcessID == pid)))
            .ToDictionary(
                t => t.pid,
                t => t.proc is null ? 0L : (long)(t.proc.StartTimeRelativeMsec * 1000));
        var pidSet = new HashSet<int>(processStart.Keys);
        var buckets = pidSet.ToDictionary(p => p, _ => new List<ImageLoadRow>());

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ImageLoad += data =>
            {
                if (!pidSet.Contains(data.ProcessID)) return;
                var tsUs = (long)(data.TimeStampRelativeMSec * 1000);
                buckets[data.ProcessID].Add(new ImageLoadRow(
                    TimeUs: tsUs,
                    TimeFromProcessStartUs: tsUs - processStart[data.ProcessID],
                    FileName: data.FileName ?? "<unknown>",
                    ImageSize: data.ImageSize));
            };
        });

        foreach (var pid in pidSet)
            buckets[pid].Sort((a, b) => a.TimeUs.CompareTo(b.TimeUs));
        return buckets;
    }

    public static ImageLoadTimingResponse PerProcess(TraceLog trace, int pid, int top)
    {
        if (top <= 0) throw new ArgumentOutOfRangeException(nameof(top));

        var process = trace.Processes.FirstOrDefault(p => p.ProcessID == pid)
            ?? throw new ArgumentException($"PID {pid} not found in trace", nameof(pid));

        var processStartUs = (long)(process.StartTimeRelativeMsec * 1000);
        var loads = new List<ImageLoadRow>();

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ImageLoad += data =>
            {
                if (data.ProcessID != pid) return;
                var tsUs = (long)(data.TimeStampRelativeMSec * 1000);
                loads.Add(new ImageLoadRow(
                    TimeUs: tsUs,
                    TimeFromProcessStartUs: tsUs - processStartUs,
                    FileName: data.FileName ?? "<unknown>",
                    ImageSize: data.ImageSize));
            };
        });

        var ordered = loads.OrderBy(l => l.TimeUs).ToList();
        var totalLoads = ordered.Count;
        var truncated = ordered.Take(top).ToList();

        var warnings = new List<string>();
        if (totalLoads == 0)
        {
            warnings.Add(
                "No ImageLoad events found for this PID. Either the process loaded no DLLs after the trace " +
                "started (rare), or the capture profile omitted the Loader keyword (default WPR profiles include it).");
        }

        return new ImageLoadTimingResponse(
            Pid: pid,
            ProcessName: process.Name ?? string.Empty,
            ProcessStartUs: processStartUs,
            TotalImageLoads: totalLoads,
            Loads: truncated,
            Warnings: warnings);
    }
}
