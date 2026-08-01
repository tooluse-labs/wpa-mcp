using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Stacks;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

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
// Requires the Microsoft-Windows-DotNETRuntime ETW provider with the Exception keyword in the
// capture profile.
public static class ClrExceptionStackAnalysis
{
    public static ClrExceptionStacksResponse TopStacks(
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
        var totalMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
            .Where(_ => ctx.StackCoverage.TotalEventCount > 0)
            .OrderByDescending(n => n.ExclusiveMetric)
            .Take(top)
            .Select(n => new ClrExceptionStackRow(
                Function: n.Name,
                ExclusiveCount: (long)n.ExclusiveMetric,
                InclusiveCount: (long)n.InclusiveMetric,
                ExclusivePct: StackSourceTopN.Pct(totalMetric, n.ExclusiveMetric),
                InclusivePct: StackSourceTopN.Pct(totalMetric, n.InclusiveMetric),
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalCount, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalCount, n.InclusiveMetric)))
            .ToList();

        return new ClrExceptionStacksResponse(
            Rows: rows,
            TotalEventCount: ctx.TotalCount,
            TopTypes: ctx.TopTypes,
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
            ctx.Normalized, focusFunction, top, metricName: "exceptions", ctx.Stats, ctx.Warnings,
            sourceTotalMetric: ctx.TotalCount,
            stackCoverage: ctx.StackCoverage,
            resultContract: contract);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalCount,
        long TraceEventCount,
        long TotalCount,
        IReadOnlyList<ClrExceptionTypeRow> TopTypes,
        DomainStackCoverage StackCoverage,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(trace, req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace, "clr_exception", "count");
        long traceTotalCount = 0;
        long traceEventCount = 0;
        long totalCount = 0;
        var countByType = new Dictionary<string, long>(StringComparer.Ordinal);

        void Handle(ExceptionTraceData data)
        {
            traceTotalCount++;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            traceEventCount++;
            if (!req.PassesFilter(data.ProcessID, nowUs)) return;

            totalCount++;
            var typeName = string.IsNullOrEmpty(data.ExceptionType) ? "(unknown)" : data.ExceptionType;
            countByType[typeName] = countByType.GetValueOrDefault(typeName) + 1;
            raw.AddSample(data.CallStackIndex(), data, 1);
            req.When.Add(nowUs, 1);
        }

        ClrEventWalker.Walk(trace, clr => clr.ExceptionStart += Handle);
        raw.Source.DoneAddingSamples();

        var lookupAttempt = StackSourceTopN.TryLookupWarmSymbols(
            raw.Source, req.ResolveSymbols, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw, lookupAttempt);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);
        var coverage = raw.Coverage.Snapshot();

        var topTypes = StackSourceTopN.TopByValue(countByType, 20, (k, v) => new ClrExceptionTypeRow(k, v));

        var warnings = new List<string>();
        if (totalCount == 0 && !req.HasFilter)
            warnings.Add(WarningBuilder.MissingClrKeyword("exception", "Exception",
                "or no exceptions were thrown in the filter window"));
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate is { } resolutionRate && resolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(resolutionRate));
        StackSourceTopN.AddCoverageWarning(warnings, coverage);
        StackSourceTopN.AddSymbolLookupWarning(warnings, stats);

        return new BuildContext(normalized, stats, traceTotalCount, traceEventCount, totalCount, topTypes, coverage, warnings);
    }
}
