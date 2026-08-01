using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// Top stacks ranked by NT-heap (HeapAlloc / RtlAllocateHeap) bytes — PerfView's
// "HeapAllocStacks" view. It shows call chains driving observed allocations from the
// user-mode heap (RtlAllocateHeap, malloc, new, std::vector
// resizes, GlobalAlloc — anything that ends in NT heap allocation).  Distinct from
// VirtualAlloc, which is page-granular reservations of address space that the heap allocator
// then sub-allocates from.
//
// Sample weight = AllocSize per HeapAlloc, NewAllocSize per HeapReAlloc.  Free events do not
// carry a size on the wire (the kernel only logs the freed address) and so cannot contribute
// to this byte metric. This analyzer does not reconstruct address retention and therefore
// cannot establish a leak.
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
            .Select(n => new HeapAllocStackRow(
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

        return new HeapAllocStacksResponse(
            Rows: rows,
            TotalBytes: ctx.TotalBytes,
            TotalEventCount: ctx.TotalEvents,
            AllocBytes: ctx.AllocBytes,
            ReallocBytes: ctx.ReallocBytes,
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
            ctx.Normalized, focusFunction, top, metricName: "heapBytes", ctx.Stats, ctx.Warnings,
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
        long TotalEvents,
        long AllocBytes,
        long ReallocBytes,
        DomainStackCoverage StackCoverage,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(trace, req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace, "heap_alloc", "bytes");
        long traceTotalBytes = 0;
        long traceEventCount = 0;
        long totalBytes = 0;
        long totalEvents = 0;
        long allocBytes = 0;
        long reallocBytes = 0;

        void Sample(int processId, long bytes, double tsRelMs, TraceEvent ev, bool isRealloc)
        {
            bytes = Math.Max(0, bytes);
            traceTotalBytes += bytes;
            var nowUs = (long)(tsRelMs * 1000);
            traceEventCount++;
            if (!req.PassesFilter(processId, nowUs)) return;

            totalBytes += bytes;
            totalEvents++;
            if (isRealloc) reallocBytes += bytes;
            else allocBytes += bytes;
            raw.AddSample(ev.CallStackIndex(), ev, bytes);
            req.When.Add(nowUs, bytes);
        }

        var source = trace.Events.GetSource();
        var heap = new HeapTraceProviderTraceEventParser(source);
        heap.HeapTraceAlloc   += d => Sample(d.ProcessID, d.AllocSize, d.TimeStampRelativeMSec, d, isRealloc: false);
        heap.HeapTraceReAlloc += d => Sample(d.ProcessID, d.NewAllocSize, d.TimeStampRelativeMSec, d, isRealloc: true);
        source.Process();
        raw.Source.DoneAddingSamples();

        var lookupAttempt = StackSourceTopN.TryLookupWarmSymbols(
            raw.Source, req.ResolveSymbols, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw, lookupAttempt);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);
        var coverage = raw.Coverage.Snapshot();

        var warnings = new List<string>();
        if (totalEvents == 0 && !req.HasFilter)
            warnings.Add(WarningBuilder.MissingPerProcessHeapTrace);
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate is { } resolutionRate && resolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(resolutionRate));
        StackSourceTopN.AddCoverageWarning(warnings, coverage);
        StackSourceTopN.AddSymbolLookupWarning(warnings, stats);

        return new BuildContext(normalized, stats, traceTotalBytes, traceEventCount, totalBytes, totalEvents, allocBytes, reallocBytes, coverage, warnings);
    }
}
