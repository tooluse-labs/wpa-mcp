using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by registry-operation count — PerfView's "Registry Stacks" view.
// Reports the call chains that drove registry queries / opens / sets, useful for
// "who's pounding the registry on every hot-path call" / "where are these lookups
// coming from" questions.
//
// Sample weight = 1 per operation (no natural byte metric for registry).  CallTree.
// ExclusiveMetric reads as "exclusive ops by this frame".  We count the "interesting"
// operations (Query, Open, Create, SetValue, DeleteValue, Delete, EnumerateKey,
// EnumerateValueKey) and skip the housekeeping events (KCB rundown, Flush, Close).
//
// Requires the Registry kernel keyword in the capture profile.  Default WPR 'CPU' /
// 'CPU.light' profiles do NOT enable it; 'GeneralProfile' or a custom .wprp does.
public static class RegistryStackAnalysis
{
    public static RegistryStacksResponse TopStacks(
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
        var totalMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveMetric)
            .Take(top)
            .Select(n => new RegistryStackRow(
                Function: n.Name,
                ExclusiveOps: (long)n.ExclusiveMetric,
                InclusiveOps: (long)n.InclusiveMetric,
                ExclusivePct: StackSourceTopN.Pct(totalMetric, n.ExclusiveMetric),
                InclusivePct: StackSourceTopN.Pct(totalMetric, n.InclusiveMetric),
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalOps, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalOps, n.InclusiveMetric)))
            .ToList();

        return new RegistryStacksResponse(
            Rows: rows,
            TotalOps: ctx.TotalOps,
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
            ctx.Normalized, focusFunction, top, metricName: "regOps", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalOps,
        long TotalOps,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalOps = 0;
        long totalOps = 0;

        void Handle(RegistryTraceData data)
        {
            traceTotalOps++;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (!req.PassesFilter(data.ProcessID, nowUs)) return;

            totalOps++;
            raw.AddSample(data.CallStackIndex(), data, 1);
            req.When.Add(nowUs, 1);
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.RegistryQueryValue       += Handle;
            kernel.RegistryQuery            += Handle;
            kernel.RegistryQueryMultipleValue += Handle;
            kernel.RegistryOpen             += Handle;
            kernel.RegistryCreate           += Handle;
            kernel.RegistrySetValue         += Handle;
            kernel.RegistrySetInformation   += Handle;
            kernel.RegistryDeleteValue      += Handle;
            kernel.RegistryDelete           += Handle;
            kernel.RegistryEnumerateKey     += Handle;
            kernel.RegistryEnumerateValueKey += Handle;
            kernel.RegistryVirtualize       += Handle;
        });
        raw.Source.DoneAddingSamples();

        if (req.ResolveSymbols)
            raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var warnings = new List<string>();
        if (totalOps == 0)
            warnings.Add(WarningBuilder.MissingKeyword("Registry", "Registry"));
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalOps, totalOps, warnings);
    }
}
