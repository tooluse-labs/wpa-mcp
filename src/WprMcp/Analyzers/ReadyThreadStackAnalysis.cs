using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by ReadyThread event count — answers "who unblocked this thread".
// PerfView equivalent: ReadyThread Stacks computer (a less-prominent view in PerfView UI
// but the same semantic data).  Pairs with wait_analysis: that one tells you "which thread
// blocked, on what wait reason, for how long" — this one tells you "who set the event /
// signalled the lock / completed the IO that woke the blocked thread up", closing the
// causality loop on producer→consumer / IPC chains.
//
// The stack on each DispatcherReadyThread event is the READIER's stack (the code that
// triggered the wake), not the awakened thread's.  Filter by `awakenedPid` to focus on
// "who readied threads in this PID" — by far the most common question.
//
// Metric = 1 per ready-thread event.  Hot stacks are typically locks, IPC reply paths,
// IOCP completions, ALPC reply, or signalled events.
//
// Requires the CSwitch (or ReadyThread) keyword in the capture profile — usually bundled
// with CSwitch in default kernel profiles.
public static class ReadyThreadStackAnalysis
{
    public static ReadyThreadStacksResponse TopStacks(
        TraceLog trace,
        int top,
        int? awakenedPid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        int whenBuckets = 0,
        bool? filterSpecified = null)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, whenBuckets);
        // StackAnalysisRequest.Pid is interpreted as awakenedPid here (the process whose
        // thread is being readied, not the readier).
        var req = new StackAnalysisRequest(awakenedPid, startUs, endUs, symbolLog, when)
        {
            FilterSpecified = filterSpecified,
        };
        var ctx = BuildNormalized(trace, req);

        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = ctx.Normalized };
        var totalMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
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
            When: when.Build());
    }

    public static CallerCalleeResponse CallerCallee(
        TraceLog trace,
        string focusFunction,
        int top,
        int? awakenedPid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, 0);
        var req = new StackAnalysisRequest(awakenedPid, startUs, endUs, symbolLog, when);
        var ctx = BuildNormalized(trace, req);
        return StackSourceTopN.ComputeCallerCallee(
            ctx.Normalized, focusFunction, top, metricName: "readyEvents", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalCount,
        long TotalCount,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalCount = 0;
        long totalCount = 0;

        // The DispatcherReadyThread event fires on the READIER thread — its CallStackIndex
        // is the readier's stack (the code that did the SetEvent / ReleaseSemaphore / IOCP
        // completion / etc.).  AwakenedProcessID identifies the process whose thread is
        // about to wake; req.Pid (== awakenedPid here) filters to "who readied threads in
        // process X".
        void Handle(DispatcherReadyThreadTraceData data)
        {
            traceTotalCount++;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
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

        if (req.ResolveSymbols)
            raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var warnings = new List<string>();
        if (totalCount == 0)
            warnings.Add(WarningBuilder.NoEventsInDefaultProfile("DispatcherReadyThread", "CSwitch / ReadyThread"));
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalCount, totalCount, warnings);
    }
}
