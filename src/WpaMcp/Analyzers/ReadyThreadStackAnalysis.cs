using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// Top stacks ranked by ReadyThread event count. PerfView equivalent: ReadyThread Stacks
// computer (a less-prominent view in PerfView UI but the same semantic data). This is
// associated readier/wakeup stack evidence that can supplement wait_analysis.
//
// The stack on each DispatcherReadyThread event is the READIER's stack (the code that
// triggered the wake), not the awakened thread's. Events are aggregated by optional
// `awakenedPid` and the requested window; they are not paired one-to-one with a specific
// wait interval or subsequent CSwitch and cannot alone establish root cause.
//
// Metric = 1 per ready-thread event.  Hot stacks are typically locks, IPC reply paths,
// IOCP completions, ALPC reply, or signalled events.
//
// Requires the CSwitch (or ReadyThread) keyword in the capture profile — usually bundled
// with CSwitch in default kernel profiles.
public static class ReadyThreadStackAnalysis
{
    internal const string AssociationOnlyWarning =
        "association_only: ReadyThread stacks are aggregated by awakenedPid (when provided) " +
        "and the requested window. They are associated readier/wakeup stack evidence, not paired " +
        "one-to-one with a specific wait interval or subsequent CSwitch, and cannot alone establish root cause.";

    public static ReadyThreadStacksResponse TopStacks(
        TraceLog trace,
        int top,
        int? awakenedPid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        int whenBuckets = 0,
        bool? filterSpecified = null,
        long? awakenedProcessStartUs = null)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, whenBuckets);
        // StackAnalysisRequest.Pid is interpreted as awakenedPid here (the process whose
        // thread is being readied, not the readier).
        var req = StackAnalysisRequest.ForProcess(
            trace, awakenedPid, awakenedProcessStartUs, startUs, endUs,
            symbolLog, when, filterSpecified);
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
            .Select(n => new ReadyThreadStackRow(
                Function: n.Name,
                ExclusiveReadyCount: (long)n.ExclusiveMetric,
                InclusiveReadyCount: (long)n.InclusiveMetric,
                ExclusivePct: StackSourceTopN.Pct(totalMetric, n.ExclusiveMetric),
                InclusivePct: StackSourceTopN.Pct(totalMetric, n.InclusiveMetric),
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalCount, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalCount, n.InclusiveMetric)))
            .ToList();

        return new ReadyThreadStacksResponse(
            Rows: rows,
            TotalReadyCount: ctx.TotalCount,
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
        int? awakenedPid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        long? awakenedProcessStartUs = null,
        bool? filterSpecified = null)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, 0);
        var req = StackAnalysisRequest.ForProcess(
            trace, awakenedPid, awakenedProcessStartUs, startUs, endUs,
            symbolLog, when, filterSpecified);
        var ctx = BuildNormalized(trace, req);
        var contract = StackResultContract.From(
            req.ProcessScope, req.HasFilter, ctx.StackCoverage,
            traceEventCount: ctx.TraceEventCount);
        return StackSourceTopN.ComputeCallerCallee(
            ctx.Normalized, focusFunction, top, metricName: "readyEvents", ctx.Stats, ctx.Warnings,
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
        DomainStackCoverage StackCoverage,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(trace, req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace, "ready_thread", "count");
        long traceTotalCount = 0;
        long traceEventCount = 0;
        long totalCount = 0;

        // The DispatcherReadyThread event fires on the READIER thread — its CallStackIndex
        // is the associated readier stack. AwakenedProcessID identifies the process whose
        // thread is about to wake; req.Pid (== awakenedPid here) scopes the aggregation.
        void Handle(DispatcherReadyThreadTraceData data)
        {
            traceTotalCount++;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (req.PassesFilter(nowUs)) traceEventCount++;
            // ReadyThread compares against AwakenedProcessID (the readied process), not the
            // readier — req.Pid is `awakenedPid` here per the analyzer's public API.
            if (!req.PassesFilter(data.AwakenedProcessID, nowUs)) return;

            totalCount++;
            raw.AddSample(data.CallStackIndex(), data, 1);
            req.When.Add(nowUs, 1);
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.DispatcherReadyThread += Handle;
        });
        raw.Source.DoneAddingSamples();

        var lookupAttempt = StackSourceTopN.TryLookupWarmSymbols(
            raw.Source, req.ResolveSymbols, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw, lookupAttempt);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);
        var coverage = raw.Coverage.Snapshot();

        var warnings = new List<string> { AssociationOnlyWarning };
        if (totalCount == 0 && !req.HasFilter)
            warnings.Add(WarningBuilder.NoEventsInDefaultProfile("DispatcherReadyThread", "CSwitch / ReadyThread"));
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate is { } resolutionRate && resolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(resolutionRate));
        StackSourceTopN.AddCoverageWarning(warnings, coverage);
        StackSourceTopN.AddSymbolLookupWarning(warnings, stats);

        return new BuildContext(normalized, stats, traceTotalCount, traceEventCount, totalCount, coverage, warnings);
    }
}
