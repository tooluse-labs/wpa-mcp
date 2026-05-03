using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by NT-heap (HeapAlloc / RtlAllocateHeap) bytes — PerfView's
// "HeapAllocStacks" view.  The canonical native-heap-leak finder: shows the call chains
// driving allocations from the user-mode heap (RtlAllocateHeap, malloc, new, std::vector
// resizes, GlobalAlloc — anything that ends in NT heap allocation).  Distinct from
// VirtualAlloc, which is page-granular reservations of address space that the heap allocator
// then sub-allocates from.
//
// Sample weight = AllocSize per HeapAlloc, NewAllocSize per HeapReAlloc.  Free events do not
// carry a size on the wire (the kernel only logs the freed address) and so cannot contribute
// to a byte metric — to find leaks, look at allocations that never had a corresponding Free
// at the same address (analysis tools reconstruct that via address pairing; we just surface
// the allocation hot stacks here).
//
// Requires the Heap kernel keyword in the capture profile, AND it has to be enabled per
// process (the heap provider is not a global flag — you ask Windows to "trace heap calls
// for process X"; see PerfView /HeapTrace flag or the equivalent .wprp <Heap> element).
public static class HeapAllocStackAnalysis
{
    public static HeapAllocStacksResponse TopStacks(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        int whenBuckets = 0)
    {
        var hasFilter = pid.HasValue || startUs.HasValue || endUs.HasValue;
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, whenBuckets);
        var ctx = BuildNormalized(trace, pid, startUs, endUs, symbolLog, when);

        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = ctx.Normalized };
        var totalBytesMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveMetric)
            .Take(top)
            .Select(n => new HeapAllocStackRow(
                Function: n.Name,
                ExclusiveBytes: (long)n.ExclusiveMetric,
                InclusiveBytes: (long)n.InclusiveMetric,
                ExclusiveEventCount: (long)n.ExclusiveCount,
                InclusiveEventCount: (long)n.InclusiveCount,
                ExclusivePct: 100.0 * n.ExclusiveMetric / totalBytesMetric,
                InclusivePct: 100.0 * n.InclusiveMetric / totalBytesMetric,
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, ctx.TraceTotalBytes, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, ctx.TraceTotalBytes, n.InclusiveMetric)))
            .ToList();

        return new HeapAllocStacksResponse(
            Rows: rows,
            TotalBytes: ctx.TotalBytes,
            TotalEventCount: ctx.TotalEvents,
            AllocBytes: ctx.AllocBytes,
            ReallocBytes: ctx.ReallocBytes,
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
        var ctx = BuildNormalized(trace, pid, startUs, endUs, symbolLog, when);
        return StackSourceTopN.ComputeCallerCallee(
            ctx.Normalized, focusFunction, top, metricName: "heapBytes", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalBytes,
        long TotalBytes,
        long TotalEvents,
        long AllocBytes,
        long ReallocBytes,
        List<string> Warnings);

    private static BuildContext BuildNormalized(
        TraceLog trace,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        StackSourceTopN.WhenHistogram when)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(symbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalBytes = 0;
        long totalBytes = 0;
        long totalEvents = 0;
        long allocBytes = 0;
        long reallocBytes = 0;

        void Sample(int processId, long bytes, double tsRelMs, TraceEvent ev, bool isRealloc)
        {
            if (bytes <= 0) return;
            traceTotalBytes += bytes;
            var nowUs = (long)(tsRelMs * 1000);
            if (pid is { } p && processId != p) return;
            if (startUs is { } s && nowUs < s) return;
            if (endUs is { } e && nowUs > e) return;

            totalBytes += bytes;
            totalEvents++;
            if (isRealloc) reallocBytes += bytes;
            else allocBytes += bytes;
            raw.AddSample(ev.CallStackIndex(), ev, bytes);
            when.Add(nowUs, bytes);
        }

        var source = trace.Events.GetSource();
        var heap = new HeapTraceProviderTraceEventParser(source);
        heap.HeapTraceAlloc   += d => Sample(d.ProcessID, d.AllocSize, d.TimeStampRelativeMSec, d, isRealloc: false);
        heap.HeapTraceReAlloc += d => Sample(d.ProcessID, d.NewAllocSize, d.TimeStampRelativeMSec, d, isRealloc: true);
        source.Process();
        raw.Source.DoneAddingSamples();

        raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var warnings = new List<string>();
        if (totalEvents == 0)
            warnings.Add(
                "No NT-heap events matched.  The Heap provider is per-process — it has to be " +
                "explicitly enabled for the target process at capture time (PerfView's " +
                "/HeapTrace flag or a .wprp <Heap> element listing the process name).  " +
                "Default WPR profiles do NOT enable it.");
        if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalBytes, totalBytes, totalEvents, allocBytes, reallocBytes, warnings);
    }
}
