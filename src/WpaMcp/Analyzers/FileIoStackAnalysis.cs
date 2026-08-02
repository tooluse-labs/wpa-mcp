using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// Top stacks ranked by file-IO bytes — PerfView's "File I/O Stacks" view. Pairs with
// FileIoAnalysis.TopFiles (per-file bucket): the per-file view answers "which files dominated
// IO", this per-stack view answers "which call chain is reading/writing them" — distinguishes
// code that legitimately streams a single big file from code that opens-reads-closes thousands
// of small files in a loop.
//
// Sample weight = IoSize (bytes per read/write). A parallel checked Int64 projection reports
// exact per-frame bytes and operation counts without round-tripping through StackSource's
// float metric.
//
// Note vs HardFaultByFileAnalysis: file IO events fire on the syscall (NtReadFile / NtWriteFile),
// so they capture both cache-hit and cache-miss reads.  MemoryHardFault only fires on cache-miss
// page-ins.  To diagnose "is my IO actually hitting disk", combine this view with
// hard_fault_top_stacks — what's in file IO but not hard-faults is cache-served.
public static class FileIoStackAnalysis
{
    public static FileIoStacksResponse TopIoStacks(
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

        var exact = StackSourceTopN.ComputeExactFrameMetrics(ctx.Normalized);
        var totalBytesMetric = Math.Max(1L, exact.TotalMetric);

        var rows = StackSourceTopN.RankExactFrames(exact)
            .Where(_ => ctx.StackCoverage.TotalEventCount > 0)
            .Take(top)
            .Select(n => new FileIoStackRow(
                Function: n.Function,
                ExclusiveBytes: n.ExclusiveMetric,
                InclusiveBytes: n.InclusiveMetric,
                ExclusiveOpCount: n.ExclusiveCount,
                InclusiveOpCount: n.InclusiveCount,
                ExclusivePct: StackSourceTopN.Pct(totalBytesMetric, n.ExclusiveMetric),
                InclusivePct: StackSourceTopN.Pct(totalBytesMetric, n.InclusiveMetric),
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalBytes, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalBytes, n.InclusiveMetric)))
            .ToList();

        return new FileIoStacksResponse(
            Rows: rows,
            TotalBytes: ctx.TotalBytes,
            TotalOpCount: ctx.TotalOps,
            Stats: ctx.Stats,
            Warnings: ctx.Warnings,
            When: when.Build("bytes"),
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
            ctx.Normalized, focusFunction, top, metricName: "ioBytes", ctx.Stats, ctx.Warnings,
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
        var raw = StackSourceTopN.CreateRawSource(trace, "file_io", "bytes");
        long traceTotalBytes = 0;
        long traceEventCount = 0;
        long totalBytes = 0;
        long totalOps = 0;

        void Handle(FileIOReadWriteTraceData data)
        {
            traceTotalBytes += data.IoSize;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            traceEventCount++;
            if (!req.PassesFilter(data.ProcessID, nowUs)) return;

            totalBytes += data.IoSize;
            totalOps++;
            raw.AddSample(data.CallStackIndex(), data, data.IoSize);
            req.When.Add(nowUs, data.IoSize);
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.FileIORead += Handle;
            kernel.FileIOWrite += Handle;
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
                "FileIORead/FileIOWrite", "FileIO"));
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
