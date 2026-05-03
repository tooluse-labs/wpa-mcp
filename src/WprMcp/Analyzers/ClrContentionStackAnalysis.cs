using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by .NET monitor-contention μs — PerfView's "Monitor Contention Stacks" view.
// Reports the call chains where threads blocked waiting on a managed lock (`lock` / `Monitor.Enter`).
// Useful for "where's our lock hotspot" / "is this slow because of a single contended monitor".
//
// CLR fires ContentionStart when a thread starts WAITING for a contended lock and ContentionStop
// when it acquires.  We match by ThreadID (a thread can only be waiting on one monitor at a time)
// and record the call stack from ContentionStart — the user's lock-acquire site.  Metric is the
// wait duration in microseconds; ContentionStop carries DurationNs directly, no clock arithmetic
// needed.
//
// Filters to Managed contention only (ContentionFlags.Managed = 0).  Native lock contention
// fires the same events with ContentionFlags.Native — it's surfaced by different tools (it's
// kernel-side); skipping here matches PerfView's view.
//
// Requires the Microsoft-Windows-DotNETRuntime ETW provider with the Contention keyword in the
// capture profile.
public static class ClrContentionStackAnalysis
{
    public static ClrContentionStacksResponse TopStacks(
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
            .Select(n => new ClrContentionStackRow(
                Function: n.Name,
                ExclusiveBlockedUs: (long)n.ExclusiveMetric,
                InclusiveBlockedUs: (long)n.InclusiveMetric,
                ExclusiveCount: (long)n.ExclusiveCount,
                InclusiveCount: (long)n.InclusiveCount,
                ExclusivePct: 100.0 * n.ExclusiveMetric / totalMetric,
                InclusivePct: 100.0 * n.InclusiveMetric / totalMetric,
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, ctx.TraceTotalUs, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, ctx.TraceTotalUs, n.InclusiveMetric)))
            .ToList();

        return new ClrContentionStacksResponse(
            Rows: rows,
            TotalBlockedUs: ctx.TotalUs,
            TotalEventCount: ctx.TotalCount,
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
            ctx.Normalized, focusFunction, top, metricName: "contentionUs", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalUs,
        long TotalUs,
        long TotalCount,
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
        // (pid, tid) → (start stack, start time).  Per-thread because contention is serialized
        // per thread; per-process too because Windows TIDs can collide across PIDs over a long
        // trace, and a Stop must only consume its own process's pending Start.
        var pendingByTid = new Dictionary<(int Pid, int Tid), (CallStackIndex Stack, long StartUs)>();
        long traceTotalUs = 0;
        long totalUs = 0;
        long totalCount = 0;

        ClrEventWalker.Walk(trace, clr =>
        {
            clr.ContentionStart += data =>
            {
                if (data.ContentionFlags != ContentionFlags.Managed) return;
                var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
                pendingByTid[(data.ProcessID, data.ThreadID)] = (data.CallStackIndex(), nowUs);
            };

            clr.ContentionStop += data =>
            {
                if (data.ContentionFlags != ContentionFlags.Managed) return;
                if (!pendingByTid.Remove((data.ProcessID, data.ThreadID), out var pending)) return;

                var us = (long)(data.DurationNs / 1000.0);
                if (us <= 0) return;
                traceTotalUs += us;
                var stopUs = (long)(data.TimeStampRelativeMSec * 1000);
                if (pid is { } p && data.ProcessID != p) return;
                // Window filter requires the contention to be entirely inside the window —
                // matches GcAnalysis's GCStart/GCStop window semantics.
                if (startUs is { } s && pending.StartUs < s) return;
                if (endUs is { } e && stopUs > e) return;

                totalUs += us;
                totalCount++;
                raw.AddSample(pending.Stack, data, us);
                when.Add(stopUs, us);
            };
        });
        raw.Source.DoneAddingSamples();

        raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var warnings = new List<string>();
        if (totalCount == 0)
            warnings.Add(WarningBuilder.MissingClrKeyword("contention", "Contention",
                "or no managed lock contention occurred in the filter window"));
        if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalUs, totalUs, totalCount, warnings);
    }
}
