using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// "How long did each fork take" view. Given a parent PID, returns each child process the
// kernel reported as having that parent, with two key timing signals per child:
//
//   FirstImageLoadOffsetUs — the kernel-side gap from ProcessStart event to the first DLL
//     mapped into the new address space. On AV-heavy hosts this typically dominates startup
//     cost: process-creation notify callbacks (PsSetCreateProcessNotifyRoutineEx) run while
//     the new process's threads don't exist yet, so this gap doesn't show up in any
//     wait_analysis row for the child — it's invisible without measuring "ProcessStart →
//     first ImageLoad" specifically. Common observation on dual-AV-equipped hosts: 800 ms+
//     per child, sometimes seconds when concurrent forks contend on the minifilter chain.
//
//   GapFromPreviousSpawnUs — wall time between consecutive sibling spawns. Useful for
//     spotting burst patterns (parent fires N children in close succession, all racing
//     through AV scans on the same minifilter chain → contention) vs steady-state (one
//     child every M seconds → no contention).
//
// Aggregate stats (median / p95 / max) across all kernel gaps surface the worst-case in a
// single number for tooling. PerfView/WPA don't have a dedicated "fork timing" view; this
// analyzer is original to wpa-mcp.
public static class ProcessCreateTimingAnalysis
{
    internal const long SlowFirstImageLoadGapUs = 1_000_000;
    internal const long VerySlowFirstImageLoadGapUs = 5_000_000;

    public static ProcessCreateTimingResponse Analyze(TraceLog trace, int parentPid, int top)
    {
        if (top <= 0) throw new ArgumentOutOfRangeException(nameof(top));

        var parent = trace.Processes.FirstOrDefault(p => p.ProcessID == parentPid);
        var warnings = new List<string>();

        var children = trace.Processes
            .Where(p => p.ParentID == parentPid)
            .OrderBy(p => p.StartTimeRelativeMsec)
            .ToList();

        if (children.Count == 0)
        {
            warnings.Add(
                $"No children found with ParentID={parentPid}. Either the parent didn't fork " +
                "anything in the captured window, or trace.Processes hasn't seen the ProcessStart " +
                "events for the children. Check list_processes output for known PIDs and their ParentPid.");
            return Empty(parentPid, parent?.Name, warnings);
        }

        // Single trace pass via ImageLoadAnalysis.ForPids — same walk used by DiagnoseTools,
        // so we don't fork a parallel "first ImageLoad per pid" implementation. We only consume
        // the first row of each list (chronologically earliest), which is the kernel-side
        // first DLL load. Memory cost (storing all loads per child) is bounded by typical
        // fork count × loads-per-process and is negligible.
        var childPids = children.Select(c => c.ProcessID).ToList();
        var imageLoadsByPid = ImageLoadAnalysis.ForPids(trace, childPids);

        var rows = new List<ChildSpawnTiming>(children.Count);
        long? prevStartUs = null;
        foreach (var c in children)
        {
            var startUs = (long)(c.StartTimeRelativeMsec * 1000);
            long? firstLoadOffset = imageLoadsByPid.TryGetValue(c.ProcessID, out var loads) && loads.Count > 0
                ? loads[0].TimeUs - startUs
                : (long?)null;

            rows.Add(new ChildSpawnTiming(
                Pid: c.ProcessID,
                Name: c.Name ?? string.Empty,
                StartTimeUs: startUs,
                FirstImageLoadOffsetUs: firstLoadOffset,
                ImageLoadCount: c.LoadedModules.Count(),
                GapFromPreviousSpawnUs: prevStartUs.HasValue ? startUs - prevStartUs.Value : (long?)null));
            prevStartUs = startUs;
        }

        // Aggregate kernel-gap distribution across children that actually loaded a DLL.
        // Children with FirstImageLoadOffsetUs=null had no ImageLoad events (extremely
        // short-lived OR the trace cut them off pre-load) and would skew percentiles, so
        // we exclude them.
        var gaps = rows.Select(r => r.FirstImageLoadOffsetUs)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .OrderBy(g => g)
            .ToList();

        long? median = gaps.Count > 0 ? gaps[gaps.Count / 2] : (long?)null;
        long? p95 = gaps.Count > 0 ? gaps[Math.Min(gaps.Count - 1, (int)(gaps.Count * 0.95))] : (long?)null;
        long? max = gaps.Count > 0 ? gaps[^1] : (long?)null;

        var spawnGaps = rows.Select(r => r.GapFromPreviousSpawnUs)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToList();
        long? avgSpawnGap = spawnGaps.Count > 0 ? (long)spawnGaps.Average() : (long?)null;

        var truncated = rows.Take(top).ToList();
        AddKernelGapWarnings(rows, warnings);
        if (rows.Count > truncated.Count)
        {
            warnings.Add(
                $"{rows.Count} children matched but only top {top} returned (sorted by spawn time). " +
                "Increase 'top' or page if you need the full set.");
        }

        return new ProcessCreateTimingResponse(
            ParentPid: parentPid,
            ParentName: parent?.Name,
            SpawnCount: rows.Count,
            FirstSpawnTimeUs: rows[0].StartTimeUs,
            LastSpawnTimeUs: rows[^1].StartTimeUs,
            AvgSpawnGapUs: avgSpawnGap,
            MedianKernelGapUs: median,
            P95KernelGapUs: p95,
            MaxKernelGapUs: max,
            Children: truncated,
            Warnings: warnings);
    }

    internal static void AddKernelGapWarnings(IReadOnlyList<ChildSpawnTiming> rows, IList<string> warnings)
    {
        var slowRows = rows
            .Where(row => row.FirstImageLoadOffsetUs >= SlowFirstImageLoadGapUs)
            .OrderByDescending(row => row.FirstImageLoadOffsetUs)
            .ToList();
        if (slowRows.Count == 0)
            return;

        var max = slowRows[0].FirstImageLoadOffsetUs!.Value;
        var thresholdMs = SlowFirstImageLoadGapUs / 1000;
        var severity = max >= VerySlowFirstImageLoadGapUs ? "very slow" : "slow";
        var examples = string.Join(", ", slowRows.Take(5).Select(row =>
            $"{row.Name}({row.Pid})={row.FirstImageLoadOffsetUs!.Value / 1000}ms"));
        warnings.Add(
            $"Detected {slowRows.Count} {severity} child process first-image-load gap(s) >= {thresholdMs}ms " +
            $"(max {max / 1000}ms; examples: {examples}). " +
            "This gap is before user-mode code can run and usually points to process-creation callbacks, AV/EDR scanning, or minifilter contention.");
    }

    private static ProcessCreateTimingResponse Empty(int parentPid, string? parentName, List<string> warnings)
        => new(
            ParentPid: parentPid,
            ParentName: parentName,
            SpawnCount: 0,
            FirstSpawnTimeUs: null,
            LastSpawnTimeUs: null,
            AvgSpawnGapUs: null,
            MedianKernelGapUs: null,
            P95KernelGapUs: null,
            MaxKernelGapUs: null,
            Children: new List<ChildSpawnTiming>(),
            Warnings: warnings);
}
