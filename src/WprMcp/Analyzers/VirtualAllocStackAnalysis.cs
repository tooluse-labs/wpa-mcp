using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by VirtualAlloc bytes — PerfView's "VirtualAlloc Stacks" view. Reports
// the call chains that drove virtual-memory reservations / commits, distinct from physical
// memory residence.  Useful for "who's reserving 4 GB of address space" / "who's leaking
// VirtualAllocs" questions.
//
// Sample weight = allocation Length (bytes).  CallTree.ExclusiveMetric reads as "exclusive
// bytes of virtual memory operated on by this frame"; ExclusiveCount tracks the operation
// count on the same stack source.
//
// Requires the VirtualAlloc kernel keyword in the capture profile.  Default WPR 'CPU' / 'CPU.light'
// profiles do NOT enable it; 'GeneralProfile' or a custom .wprp does.
public static class VirtualAllocStackAnalysis
{
    public static VirtualAllocStacksResponse TopStacks(
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
            .Select(n => new VirtualAllocStackRow(
                Function: n.Name,
                ExclusiveBytes: (long)n.ExclusiveMetric,
                InclusiveBytes: (long)n.InclusiveMetric,
                ExclusiveOpCount: (long)n.ExclusiveCount,
                InclusiveOpCount: (long)n.InclusiveCount,
                ExclusivePct: 100.0 * n.ExclusiveMetric / totalBytesMetric,
                InclusivePct: 100.0 * n.InclusiveMetric / totalBytesMetric,
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, ctx.TraceTotalBytes, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, ctx.TraceTotalBytes, n.InclusiveMetric)))
            .ToList();

        return new VirtualAllocStacksResponse(
            Rows: rows,
            TotalBytes: ctx.TotalBytes,
            TotalOpCount: ctx.TotalOps,
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
            ctx.Normalized, focusFunction, top, metricName: "vallocBytes", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalBytes,
        long TotalBytes,
        long TotalOps,
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
        long totalOps = 0;

        // Free events are folded into the same metric — "what code touched virtual memory"
        // includes deallocation paths.  Filter at the consumer if you need alloc-only.
        void Handle(VirtualAllocTraceData data)
        {
            var bytes = (long)data.Length;
            if (bytes == 0) return; // 0-byte ops would inflate ExclusiveCount with no metric
            traceTotalBytes += bytes;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (pid is { } p && data.ProcessID != p) return;
            if (startUs is { } s && nowUs < s) return;
            if (endUs is { } e && nowUs > e) return;

            totalBytes += bytes;
            totalOps++;
            raw.AddSample(data.CallStackIndex(), data, bytes);
            when.Add(nowUs, bytes);
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.VirtualMemAlloc += Handle;
            kernel.VirtualMemFree += Handle;
        });
        raw.Source.DoneAddingSamples();

        raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var warnings = new List<string>();
        if (totalOps == 0)
            warnings.Add(WarningBuilder.MissingKeyword("VirtualMemAlloc/VirtualMemFree", "VirtualAlloc"));
        if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalBytes, totalBytes, totalOps, warnings);
    }
}
