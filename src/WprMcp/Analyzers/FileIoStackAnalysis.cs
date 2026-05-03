using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by file-IO bytes — PerfView's "File I/O Stacks" view. Pairs with
// FileIoAnalysis.TopFiles (per-file bucket): the per-file view answers "which files dominated
// IO", this per-stack view answers "which call chain is reading/writing them" — distinguishes
// code that legitimately streams a single big file from code that opens-reads-closes thousands
// of small files in a loop.
//
// Sample weight = IoSize (bytes per read/write). CallTree.ExclusiveMetric reads as "exclusive
// bytes processed by this frame"; ExclusiveCount tracks the operation count for free on the
// same stack source — no need for a separate count-only pipeline.
//
// Note vs MmapAnalysis: file IO events fire on the syscall (NtReadFile / NtWriteFile), so
// they capture both cache-hit and cache-miss reads. MemoryHardFault only fires on cache-miss
// page-ins. To diagnose "is my IO actually hitting disk", combine this view with mmap_top_stacks
// (page-in stacks) — what's in file IO but not hard-faults is cache-served.
public static class FileIoStackAnalysis
{
    public static FileIoStacksResponse TopIoStacks(
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
            .Select(n => new FileIoStackRow(
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

        return new FileIoStacksResponse(
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
            ctx.Normalized, focusFunction, top, metricName: "ioBytes", ctx.Stats, ctx.Warnings);
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

        void Handle(FileIOReadWriteTraceData data)
        {
            traceTotalBytes += data.IoSize;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (pid is { } p && data.ProcessID != p) return;
            if (startUs is { } s && nowUs < s) return;
            if (endUs is { } e && nowUs > e) return;

            totalBytes += data.IoSize;
            totalOps++;
            raw.AddSample(data.CallStackIndex(), data, data.IoSize);
            when.Add(nowUs, data.IoSize);
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.FileIORead += Handle;
            kernel.FileIOWrite += Handle;
        });
        raw.Source.DoneAddingSamples();

        raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var warnings = new List<string>();
        if (totalOps == 0)
        {
            warnings.Add(
                "No FileIORead/FileIOWrite events matched. The capture profile likely omits the FileIO " +
                "keyword (default WPR 'CPU' / 'CPU.light' profiles do); use 'FileIO.light' or a custom " +
                ".wprp that enables it.");
        }
        if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalBytes, totalBytes, totalOps, warnings);
    }
}
