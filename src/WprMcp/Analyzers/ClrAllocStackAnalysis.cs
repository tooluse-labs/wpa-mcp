using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by managed-heap allocation bytes — PerfView's "GC Heap Alloc Stacks" view.
// Reports the call chains that drove .NET object allocation, the canonical way to find managed
// allocation hotspots ("who's allocating 2 GB of strings on the request hot path").
//
// Sample weight = bytes per GCAllocationTick.  The CLR fires GCAllocationTick roughly every
// ~100 KB allocated per (heap, generation, type), with AllocationAmount64 = bytes since the
// previous tick for that bucket.  This is sampling, not exhaustive — it's the same trade-off
// PerfView's view makes (low overhead, statistically meaningful for the hot allocators).
//
// Newer runtimes (.NET 8+) also fire AllocationSampling — a higher-fidelity sampled event with
// per-object size.  We don't subscribe to it: when both are enabled they double-count, and tick
// is the canonical default that's been on every CLR since 4.0.
//
// Requires the Microsoft-Windows-DotNETRuntime ETW provider with the GC keyword (0x1) in the
// capture profile.  WPR profiles need an explicit <EventCollectorId> for the runtime provider.
public static class ClrAllocStackAnalysis
{
    public static ClrAllocStacksResponse TopStacks(
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
            .Select(n => new ClrAllocStackRow(
                Function: n.Name,
                ExclusiveBytes: (long)n.ExclusiveMetric,
                InclusiveBytes: (long)n.InclusiveMetric,
                ExclusiveTickCount: (long)n.ExclusiveCount,
                InclusiveTickCount: (long)n.InclusiveCount,
                ExclusivePct: 100.0 * n.ExclusiveMetric / totalBytesMetric,
                InclusivePct: 100.0 * n.InclusiveMetric / totalBytesMetric,
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, ctx.TraceTotalBytes, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, ctx.TraceTotalBytes, n.InclusiveMetric)))
            .ToList();

        return new ClrAllocStacksResponse(
            Rows: rows,
            TotalBytes: ctx.TotalBytes,
            TotalTickCount: ctx.TotalTicks,
            TopTypes: ctx.TopTypes,
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
            ctx.Normalized, focusFunction, top, metricName: "allocBytes", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalBytes,
        long TotalBytes,
        long TotalTicks,
        IReadOnlyList<ClrAllocTypeRow> TopTypes,
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
        long totalTicks = 0;
        var bytesByType = new Dictionary<string, long>(StringComparer.Ordinal);

        void Handle(GCAllocationTickTraceData data)
        {
            var bytes = data.AllocationAmount64 > 0 ? data.AllocationAmount64 : data.AllocationAmount;
            if (bytes <= 0) return;
            traceTotalBytes += bytes;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (pid is { } p && data.ProcessID != p) return;
            if (startUs is { } s && nowUs < s) return;
            if (endUs is { } e && nowUs > e) return;

            totalBytes += bytes;
            totalTicks++;
            if (!string.IsNullOrEmpty(data.TypeName))
                bytesByType[data.TypeName] = bytesByType.GetValueOrDefault(data.TypeName) + bytes;
            raw.AddSample(data.CallStackIndex(), data, bytes);
            when.Add(nowUs, bytes);
        }

        ClrEventWalker.Walk(trace, clr => clr.GCAllocationTick += Handle);
        raw.Source.DoneAddingSamples();

        raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var topTypes = bytesByType
            .OrderByDescending(kv => kv.Value)
            .Take(20)
            .Select(kv => new ClrAllocTypeRow(kv.Key, kv.Value))
            .ToList();

        var warnings = new List<string>();
        if (totalTicks == 0)
            warnings.Add(WarningBuilder.MissingClrKeyword("GC allocation", "GC",
                "or no managed allocation reached the ~100 KB tick threshold in the window"));
        if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalBytes, totalBytes, totalTicks, topTypes, warnings);
    }
}
