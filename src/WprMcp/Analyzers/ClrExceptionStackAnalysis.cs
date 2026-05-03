using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by .NET exception throw count — PerfView's "Exceptions Stacks" view.
// Reports the call chains that drove ExceptionStart events, useful for "is this code path
// throwing 1000 exceptions per second" / "where is FormatException being swallowed in a
// retry loop".  Fires once per *thrown* exception (rethrows count as separate events; caught
// vs uncaught isn't distinguished here — both ExceptionCatchStart and ExceptionStart fire,
// but only the throw is counted).
//
// Sample weight = 1 per exception.  We also surface a `TopTypes` summary (top exception type
// names by count) so consumers don't need to follow up with another tool to answer "what
// kind of exceptions are these".
//
// Requires the Microsoft-Windows-DotNETRuntime ETW provider with the Exception keyword (0x8000)
// in the capture profile.
public static class ClrExceptionStackAnalysis
{
    public static ClrExceptionStacksResponse TopStacks(
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
        var totalMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveMetric)
            .Take(top)
            .Select(n => new ClrExceptionStackRow(
                Function: n.Name,
                ExclusiveCount: (long)n.ExclusiveMetric,
                InclusiveCount: (long)n.InclusiveMetric,
                ExclusivePct: 100.0 * n.ExclusiveMetric / totalMetric,
                InclusivePct: 100.0 * n.InclusiveMetric / totalMetric,
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, ctx.TraceTotalCount, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, ctx.TraceTotalCount, n.InclusiveMetric)))
            .ToList();

        return new ClrExceptionStacksResponse(
            Rows: rows,
            TotalCount: ctx.TotalCount,
            TopTypes: ctx.TopTypes,
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
            ctx.Normalized, focusFunction, top, metricName: "exceptions", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalCount,
        long TotalCount,
        IReadOnlyList<ClrExceptionTypeRow> TopTypes,
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
        long traceTotalCount = 0;
        long totalCount = 0;
        var countByType = new Dictionary<string, long>(StringComparer.Ordinal);

        void Handle(ExceptionTraceData data)
        {
            traceTotalCount++;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (pid is { } p && data.ProcessID != p) return;
            if (startUs is { } s && nowUs < s) return;
            if (endUs is { } e && nowUs > e) return;

            totalCount++;
            var typeName = string.IsNullOrEmpty(data.ExceptionType) ? "(unknown)" : data.ExceptionType;
            countByType[typeName] = countByType.GetValueOrDefault(typeName) + 1;
            raw.AddSample(data.CallStackIndex(), data, 1);
            when.Add(nowUs, 1);
        }

        ClrEventWalker.Walk(trace, clr => clr.ExceptionStart += Handle);
        raw.Source.DoneAddingSamples();

        raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var topTypes = countByType
            .OrderByDescending(kv => kv.Value)
            .Take(20)
            .Select(kv => new ClrExceptionTypeRow(kv.Key, kv.Value))
            .ToList();

        var warnings = new List<string>();
        if (totalCount == 0)
            warnings.Add(WarningBuilder.MissingClrKeyword("exception", "Exception",
                "or no exceptions were thrown in the filter window"));
        if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalCount, totalCount, topTypes, warnings);
    }
}
