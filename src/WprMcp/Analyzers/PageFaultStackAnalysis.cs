using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by hard-fault paging-in bytes. PerfView's marquee menu doesn't include
// this view (PerfView focuses on CPU/ImageLoad/FileIO/DiskIO stacks); WPA's "Hard Faults"
// graph is the closer reference. We build it on PerfView's TraceEvent SDK because the
// underlying MutableTraceEventStackSource + CallTree machinery is what we already use for
// every other stack-based analyzer.
//
// Pairs with HardFaultByFileAnalysis, which buckets the SAME events by FileName: the per-file
// view answers "which file is paging in most", this per-stack view answers "which call chain
// is triggering those page-ins" — the question that a slow-process-creation case actually
// wants answered (eager loader vs lazy use vs scanner-induced).
//
// Sample weight = ByteCount, so ExclusiveMetric reads as "exclusive bytes paged in by this
// frame". The CallTree separately tracks ExclusiveCount (one per AddSample call), giving us
// "fault count" for free without a second stack source — important because each AddSample
// adds an entry to the interner and a second source would double symbol-resolution cost.
//
// Like HardFaultByFileAnalysis, requires the HardFaults kernel keyword in the capture profile. Default
// WPR profiles do NOT enable it — see tests/WprMcp.Tests/fixtures/MmapCapture.wprp. The
// usual MmapKeywordHint warning is emitted unconditionally so empty results are explainable.
public static class PageFaultStackAnalysis
{
    public static HardFaultStacksResponse TopFaultStacks(
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

        // CallTree on the metric-weighted source gives us BOTH dimensions for free:
        //   ExclusiveMetric = sum of ByteCount values ending at this frame ("bytes paged in")
        //   ExclusiveCount  = number of samples ending at this frame   ("faults")
        // No need for a parallel count-only stack source.
        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = ctx.Normalized };
        var totalBytesMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveMetric)
            .Take(top)
            .Select(n => new HardFaultStackRow(
                Function: n.Name,
                ExclusivePageInBytes: (long)n.ExclusiveMetric,
                InclusivePageInBytes: (long)n.InclusiveMetric,
                ExclusiveFaultCount: (long)n.ExclusiveCount,
                InclusiveFaultCount: (long)n.InclusiveCount,
                ExclusivePct: StackSourceTopN.Pct(totalBytesMetric, n.ExclusiveMetric),
                InclusivePct: StackSourceTopN.Pct(totalBytesMetric, n.InclusiveMetric),
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalBytes, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalBytes, n.InclusiveMetric)))
            .ToList();

        return new HardFaultStacksResponse(
            Rows: rows,
            TotalPageInBytes: ctx.TotalBytes,
            TotalFaultCount: ctx.TotalFaults,
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
            ctx.Normalized, focusFunction, top, metricName: "pageInBytes", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalBytes,
        long TotalBytes,
        long TotalFaults,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalBytes = 0;
        long totalBytes = 0;
        long totalFaults = 0;

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.MemoryHardFault += data =>
            {
                traceTotalBytes += data.ByteCount;
                var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
                if (!req.PassesFilter(data.ProcessID, nowUs)) return;

                totalBytes += data.ByteCount;
                totalFaults++;
                raw.AddSample(data.CallStackIndex(), data, data.ByteCount);
                req.When.Add(nowUs, data.ByteCount);
            };
        });
        raw.Source.DoneAddingSamples();

        if (req.ResolveSymbols)
            raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var warnings = new List<string> { WarningBuilder.HardFaultKeywordHint };
        if (totalFaults == 0)
        {
            warnings.Add(
                "No MemoryHardFault events matched. The capture profile likely omits the HardFaults " +
                "keyword (default WPR profiles do); see tests/WprMcp.Tests/fixtures/MmapCapture.wprp " +
                "for a profile that enables it.");
        }
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalBytes, totalBytes, totalFaults, warnings);
    }
}
