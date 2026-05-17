using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by physical disk-IO bytes — PerfView's "Disk I/O Stacks" view. Reports
// the call chains that drove genuine disk activity, distinct from FileIoStackAnalysis which
// captures all file-IO syscalls (including cache-served reads).
//
// Diff strategy: take a frame that ranks high in file_io_top_stacks but DOESN'T appear in
// disk_io_top_stacks → that code path's IO is cache-served. Inverse signal (high in disk
// but not in file) is rarer and indicates kernel-side / paging IO without a corresponding
// user-mode syscall.
//
// Sample weight = TransferSize (bytes per IO). CallTree.ExclusiveMetric reads as "exclusive
// bytes hit-disk by this frame"; ExclusiveCount tracks the operation count for free on the
// same stack source.
//
// Requires the DiskIO kernel keyword in the capture profile. Default WPR 'CPU' profiles do
// NOT enable it; FileIO.light or a custom .wprp does.
public static class DiskIoStackAnalysis
{
    public static DiskIoStacksResponse TopIoStacks(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        int whenBuckets = 0)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, whenBuckets);
        var req = new StackAnalysisRequest(pid, startUs, endUs, symbolLog, when);
        var ctx = BuildNormalized(trace, req);

        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = ctx.Normalized };
        var totalBytesMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveMetric)
            .Take(top)
            .Select(n => new DiskIoStackRow(
                Function: n.Name,
                ExclusiveBytes: (long)n.ExclusiveMetric,
                InclusiveBytes: (long)n.InclusiveMetric,
                ExclusiveOpCount: (long)n.ExclusiveCount,
                InclusiveOpCount: (long)n.InclusiveCount,
                ExclusivePct: 100.0 * n.ExclusiveMetric / totalBytesMetric,
                InclusivePct: 100.0 * n.InclusiveMetric / totalBytesMetric,
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalBytes, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalBytes, n.InclusiveMetric)))
            .ToList();

        return new DiskIoStacksResponse(
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
        var req = new StackAnalysisRequest(pid, startUs, endUs, symbolLog, when);
        var ctx = BuildNormalized(trace, req);
        return StackSourceTopN.ComputeCallerCallee(
            ctx.Normalized, focusFunction, top, metricName: "diskBytes", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalBytes,
        long TotalBytes,
        long TotalOps,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalBytes = 0;
        long totalBytes = 0;
        long totalOps = 0;

        // DiskIORead / DiskIOWrite both deliver DiskIOTraceData with TransferSize and a stack
        // walk. We share one handler since the metric and filter logic are identical.
        void Handle(DiskIOTraceData data)
        {
            traceTotalBytes += data.TransferSize;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (!req.PassesFilter(data.ProcessID, nowUs)) return;

            totalBytes += data.TransferSize;
            totalOps++;
            raw.AddSample(data.CallStackIndex(), data, data.TransferSize);
            req.When.Add(nowUs, data.TransferSize);
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.DiskIORead += Handle;
            kernel.DiskIOWrite += Handle;
        });
        raw.Source.DoneAddingSamples();

        if (req.ResolveSymbols)
            raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var warnings = new List<string>();
        if (totalOps == 0)
        {
            warnings.Add(
                "No DiskIORead/DiskIOWrite events matched. The capture profile likely omits the DiskIO " +
                "keyword (default WPR 'CPU' / 'CPU.light' profiles do); use 'FileIO.light' or a custom " +
                ".wprp that enables it. Alternatively, no IO actually hit physical disk in this window.");
        }
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalBytes, totalBytes, totalOps, warnings);
    }
}
