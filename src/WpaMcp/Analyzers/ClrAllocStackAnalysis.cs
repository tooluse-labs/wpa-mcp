using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Stacks;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

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
// Requires the Microsoft-Windows-DotNETRuntime ETW provider with the GC keyword in the
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
        int whenBuckets = 0,
        bool? filterSpecified = null)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, whenBuckets);
        var req = new StackAnalysisRequest(pid, startUs, endUs, symbolLog, when)
        {
            FilterSpecified = filterSpecified,
        };
        var ctx = BuildNormalized(trace, req);

        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = ctx.Normalized };
        var totalBytesMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveMetric)
            .Take(top)
            .Select(n => new ClrAllocStackRow(
                Function: n.Name,
                ExclusiveBytes: (long)n.ExclusiveMetric,
                InclusiveBytes: (long)n.InclusiveMetric,
                ExclusiveEventCount: (long)n.ExclusiveCount,
                InclusiveEventCount: (long)n.InclusiveCount,
                ExclusivePct: StackSourceTopN.Pct(totalBytesMetric, n.ExclusiveMetric),
                InclusivePct: StackSourceTopN.Pct(totalBytesMetric, n.InclusiveMetric),
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalBytes, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalBytes, n.InclusiveMetric)))
            .ToList();

        return new ClrAllocStacksResponse(
            Rows: rows,
            TotalBytes: ctx.TotalBytes,
            TotalEventCount: ctx.TotalEvents,
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
        var req = new StackAnalysisRequest(pid, startUs, endUs, symbolLog, when);
        var ctx = BuildNormalized(trace, req);
        return StackSourceTopN.ComputeCallerCallee(
            ctx.Normalized, focusFunction, top, metricName: "allocBytes", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalBytes,
        long TotalBytes,
        long TotalEvents,
        IReadOnlyList<ClrAllocTypeRow> TopTypes,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalBytes = 0;
        long totalBytes = 0;
        long totalEvents = 0;
        var bytesByType = new Dictionary<string, long>(StringComparer.Ordinal);

        void Handle(GCAllocationTickTraceData data)
        {
            var bytes = data.AllocationAmount64 > 0 ? data.AllocationAmount64 : data.AllocationAmount;
            if (bytes <= 0) return;
            traceTotalBytes += bytes;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (!req.PassesFilter(data.ProcessID, nowUs)) return;

            totalBytes += bytes;
            totalEvents++;
            if (!string.IsNullOrEmpty(data.TypeName))
                bytesByType[data.TypeName] = bytesByType.GetValueOrDefault(data.TypeName) + bytes;
            raw.AddSample(data.CallStackIndex(), data, bytes);
            req.When.Add(nowUs, bytes);
        }

        ClrEventWalker.Walk(trace, clr => clr.GCAllocationTick += Handle);
        raw.Source.DoneAddingSamples();

        if (req.ResolveSymbols)
            raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var topTypes = StackSourceTopN.TopByValue(bytesByType, 20, (k, v) => new ClrAllocTypeRow(k, v));

        var warnings = new List<string>();
        if (totalEvents == 0)
            warnings.Add(WarningBuilder.MissingClrKeyword("GC allocation", "GC",
                "or no managed allocation reached the ~100 KB tick threshold in the window"));
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalBytes, totalBytes, totalEvents, topTypes, warnings);
    }
}
