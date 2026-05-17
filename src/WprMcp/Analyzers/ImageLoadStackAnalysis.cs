using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by ImageLoad-event count. PerfView calls this view "Image Load Stacks"
// (src/TraceEvent/Computers/ImageLoadStackComputer.cs). Complements ImageLoadAnalysis (which
// returns the chronological list of loads): this one answers "*who* triggered the load" by
// rolling every ImageLoad event up to its stack — typically LdrLoadDll / LoadLibraryEx /
// CoCreateInstance / DllMain-cascading-load / AmsiOpenSession / EDR injection / etc.
//
// Metric = 1 per load (we want a count, not a byte-weighted sum). If a future use-case
// wants byte-weighted ranking, that's a parameter to add.
//
// Gotcha — stack walks must be enabled for ImageLoad. Default WPR profiles attach stacks
// to ProcessStart/ImageLoad; some custom .wprp files do not. When stacks are missing, every
// load lands on the synthetic "?!?" root and the response is uninformative — but rendering
// still works (PerfView-parity invariant #1).
public static class ImageLoadStackAnalysis
{
    public static ImageLoadStacksResponse TopLoadStacks(
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

        // Metric=1 per load means ExclusiveCount and ExclusiveMetric are equal — pick
        // ExclusiveCount for parity with CpuAnalysis.
        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = ctx.Normalized };
        var totalSamples = (double)Math.Max(1, callTree.Root.InclusiveCount);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveCount)
            .Take(top)
            .Select(n => new ImageLoadStackRow(
                Function: n.Name,
                ExclusiveLoads: (long)n.ExclusiveCount,
                InclusiveLoads: (long)n.InclusiveCount,
                ExclusivePct: 100.0 * n.ExclusiveCount / totalSamples,
                InclusivePct: 100.0 * n.InclusiveCount / totalSamples,
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalLoads, n.ExclusiveCount),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalLoads, n.InclusiveCount)))
            .ToList();

        return new ImageLoadStacksResponse(
            Rows: rows,
            TotalLoads: ctx.TotalLoads,
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
            ctx.Normalized, focusFunction, top, metricName: "loads", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalLoads,
        long TotalLoads,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalLoads = 0;
        long totalLoads = 0;

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ImageLoad += data =>
            {
                traceTotalLoads++;
                var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
                if (!req.PassesFilter(data.ProcessID, nowUs)) return;

                totalLoads++;
                raw.AddSample(data.CallStackIndex(), data, 1);
                req.When.Add(nowUs, 1);
            };
        });
        raw.Source.DoneAddingSamples();

        if (req.ResolveSymbols)
            raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var warnings = new List<string>();
        if (totalLoads == 0)
        {
            warnings.Add(
                "No ImageLoad events matched. Either no DLLs were mapped in this filter window, " +
                "or the capture profile omits the Loader keyword. Default WPR profiles include it.");
        }
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalLoads, totalLoads, warnings);
    }
}
