using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

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
        int whenBuckets = 0,
        bool? filterSpecified = null,
        long? processStartUs = null)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, whenBuckets);
        var req = StackAnalysisRequest.ForProcess(
            trace, pid, processStartUs, startUs, endUs, symbolLog, when, filterSpecified);
        var ctx = BuildNormalized(trace, req);
        var contract = StackResultContract.From(
            req.ProcessScope, req.HasFilter, ctx.StackCoverage,
            traceEventCount: ctx.TraceEventCount);
        contract.AddWarning(ctx.Warnings);

        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = ctx.Normalized };
        var totalBytesMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
            .Where(_ => ctx.StackCoverage.TotalEventCount > 0)
            .OrderByDescending(n => n.ExclusiveMetric)
            .Take(top)
            .Select(n => new DiskIoStackRow(
                Function: n.Name,
                ExclusiveBytes: (long)n.ExclusiveMetric,
                InclusiveBytes: (long)n.InclusiveMetric,
                ExclusiveOpCount: (long)n.ExclusiveCount,
                InclusiveOpCount: (long)n.InclusiveCount,
                ExclusivePct: StackSourceTopN.Pct(totalBytesMetric, n.ExclusiveMetric),
                InclusivePct: StackSourceTopN.Pct(totalBytesMetric, n.InclusiveMetric),
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalBytes, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalBytes, n.InclusiveMetric)))
            .ToList();

        return new DiskIoStacksResponse(
            Rows: rows,
            TotalBytes: ctx.TotalBytes,
            TotalOpCount: ctx.TotalOps,
            Stats: ctx.Stats,
            Warnings: ctx.Warnings,
            When: when.Build(),
            StackCoverage: ctx.StackCoverage,
            SelectedProcess: contract.SelectedProcess,
            ScopeMode: contract.ScopeMode,
            PidReuseObserved: contract.PidReuseObserved,
            IncludedProcesses: contract.IncludedProcesses,
            ScopeStatus: contract.ScopeStatus,
            CapabilityStatus: contract.CapabilityStatus,
            MatchedEventCount: contract.MatchedEventCount,
            NoDataReason: contract.NoDataReason);
    }

    public static CallerCalleeResponse CallerCallee(
        TraceLog trace,
        string focusFunction,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        long? processStartUs = null,
        bool? filterSpecified = null)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, 0);
        var req = StackAnalysisRequest.ForProcess(
            trace, pid, processStartUs, startUs, endUs, symbolLog, when, filterSpecified);
        var ctx = BuildNormalized(trace, req);
        var contract = StackResultContract.From(
            req.ProcessScope, req.HasFilter, ctx.StackCoverage,
            traceEventCount: ctx.TraceEventCount);
        return StackSourceTopN.ComputeCallerCallee(
            ctx.Normalized, focusFunction, top, metricName: "diskBytes", ctx.Stats, ctx.Warnings,
            sourceTotalMetric: ctx.TotalBytes,
            stackCoverage: ctx.StackCoverage,
            resultContract: contract);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalBytes,
        long TraceEventCount,
        long TotalBytes,
        long TotalOps,
        DomainStackCoverage StackCoverage,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(trace, req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace, "disk_io", "bytes");
        long traceTotalBytes = 0;
        long traceEventCount = 0;
        long totalBytes = 0;
        long totalOps = 0;

        // DiskIORead / DiskIOWrite both deliver DiskIOTraceData with TransferSize and a stack
        // walk. We share one handler since the metric and filter logic are identical.
        void Handle(DiskIOTraceData data)
        {
            traceTotalBytes += data.TransferSize;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (req.PassesFilter(nowUs)) traceEventCount++;
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

        var lookupAttempt = StackSourceTopN.TryLookupWarmSymbols(
            raw.Source, req.ResolveSymbols, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw, lookupAttempt);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);
        var coverage = raw.Coverage.Snapshot();

        var warnings = new List<string>();
        if (totalOps == 0 && !req.HasFilter)
        {
            warnings.Add(WarningBuilder.MissingKeyword(
                "DiskIORead/DiskIOWrite", "DiskIO"));
        }
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate is { } resolutionRate && resolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(resolutionRate));
        StackSourceTopN.AddCoverageWarning(warnings, coverage);
        StackSourceTopN.AddSymbolLookupWarning(warnings, stats);

        return new BuildContext(normalized, stats, traceTotalBytes, traceEventCount, totalBytes, totalOps, coverage, warnings);
    }
}
