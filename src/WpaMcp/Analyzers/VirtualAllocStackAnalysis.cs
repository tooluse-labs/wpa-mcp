using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// Top stacks ranked by VirtualAlloc operation bytes — PerfView's "VirtualAlloc Stacks" view.
// Alloc and free traffic are both positive call-tree weights. Directional exact totals are
// reported separately; neither the operation traffic nor alloc-minus-free is a live-set,
// commit, retained-memory, or leak measurement.
//
// Sample weight = event Length (bytes). CallTree.ExclusiveMetric reads as approximate
// "exclusive bytes of virtual-memory operations through this frame"; ExclusiveCount tracks
// the operation count on the same stack source. Exact directional totals bypass CallTree.
//
// Requires the VirtualAlloc kernel keyword in the capture profile.  Default WPR 'CPU' / 'CPU.light'
// profiles do NOT enable it; 'GeneralProfile' or a custom .wprp does.
internal sealed class VirtualAllocOperationAccumulator
{
    public long AllocatedBytes { get; private set; }
    public long AllocatedCount { get; private set; }
    public long FreedBytes { get; private set; }
    public long FreedCount { get; private set; }

    public long TotalOperationBytes => checked(AllocatedBytes + FreedBytes);
    public long TotalOperationCount => checked(AllocatedCount + FreedCount);
    public long NetObservedOperationBytes => checked(AllocatedBytes - FreedBytes);

    public void ObserveAllocation(long bytes) => Observe(bytes, allocation: true);
    public void ObserveFree(long bytes) => Observe(bytes, allocation: false);

    private void Observe(long bytes, bool allocation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (allocation)
        {
            AllocatedBytes = checked(AllocatedBytes + bytes);
            AllocatedCount = checked(AllocatedCount + 1);
        }
        else
        {
            FreedBytes = checked(FreedBytes + bytes);
            FreedCount = checked(FreedCount + 1);
        }
    }
}

public static class VirtualAllocStackAnalysis
{
    public static VirtualAllocStacksResponse TopStacks(
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
            .Select(n => new VirtualAllocStackRow(
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

        return new VirtualAllocStacksResponse(
            Rows: rows,
            TotalBytes: ctx.Totals.TotalOperationBytes,
            TotalOpCount: ctx.Totals.TotalOperationCount,
            Stats: ctx.Stats,
            Warnings: ctx.Warnings,
            When: when.Build(),
            StackCoverage: ctx.StackCoverage,
            AllocatedBytes: ctx.Totals.AllocatedBytes,
            AllocatedCount: ctx.Totals.AllocatedCount,
            FreedBytes: ctx.Totals.FreedBytes,
            FreedCount: ctx.Totals.FreedCount,
            TotalOperationBytes: ctx.Totals.TotalOperationBytes,
            TotalOperationCount: ctx.Totals.TotalOperationCount,
            NetObservedOperationBytes: ctx.Totals.NetObservedOperationBytes,
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
            ctx.Normalized, focusFunction, top, metricName: "virtualMemoryOperationBytes", ctx.Stats, ctx.Warnings,
            sourceTotalMetric: ctx.Totals.TotalOperationBytes,
            stackCoverage: ctx.StackCoverage,
            resultContract: contract);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalBytes,
        long TraceEventCount,
        VirtualAllocOperationAccumulator Totals,
        DomainStackCoverage StackCoverage,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(trace, req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(
            trace, "virtual_alloc", "virtualMemoryOperationBytes");
        var traceTotals = new VirtualAllocOperationAccumulator();
        long traceEventCount = 0;
        var totals = new VirtualAllocOperationAccumulator();

        void Handle(VirtualAllocTraceData data, bool allocation)
        {
            var bytes = checked((long)data.Length);
            if (allocation) traceTotals.ObserveAllocation(bytes);
            else traceTotals.ObserveFree(bytes);
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            traceEventCount++;
            if (!req.PassesFilter(data.ProcessID, nowUs)) return;

            if (allocation) totals.ObserveAllocation(bytes);
            else totals.ObserveFree(bytes);
            raw.AddSample(data.CallStackIndex(), data, bytes);
            req.When.Add(nowUs, bytes);
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.VirtualMemAlloc += data => Handle(data, allocation: true);
            kernel.VirtualMemFree += data => Handle(data, allocation: false);
        });
        raw.Source.DoneAddingSamples();

        var lookupAttempt = StackSourceTopN.TryLookupWarmSymbols(
            raw.Source, req.ResolveSymbols, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw, lookupAttempt);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);
        var coverage = raw.Coverage.Snapshot();

        var warnings = new List<string>();
        if (totals.TotalOperationCount == 0 && !req.HasFilter)
            warnings.Add(WarningBuilder.MissingKeyword("VirtualMemAlloc/VirtualMemFree", "VirtualAlloc"));
        warnings.Add(
            "virtual_alloc_metric=operation_bytes: alloc and free lengths are positive stack weights; " +
            "NetObservedOperationBytes is alloc-minus-free event traffic, not live virtual size, commit, retained memory, or leak proof.");
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate is { } resolutionRate && resolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(resolutionRate));
        StackSourceTopN.AddCoverageWarning(warnings, coverage);
        StackSourceTopN.AddSymbolLookupWarning(warnings, stats);

        return new BuildContext(
            normalized, stats, traceTotals.TotalOperationBytes, traceEventCount, totals, coverage, warnings);
    }
}
