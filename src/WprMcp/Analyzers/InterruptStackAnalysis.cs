using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by kernel interrupt time (DPC + ISR) — answers "which driver routines
// are burning kernel time".  PerfView equivalent: 'DPC/ISR Stacks' computer.  The two event
// classes:
//
//   ISR (Interrupt Service Routine): the immediate kernel response to a hardware interrupt.
//     Runs at HIGH IRQL on the interrupted CPU; should be very short.
//   DPC (Deferred Procedure Call): kernel work queued from an ISR, runs at DISPATCH_LEVEL
//     and can preempt thread scheduling.
//
// Both events carry an `ElapsedTimeMSec` field — the time the routine spent running.  We
// sum across both into a single "interrupt time" metric (microseconds), so a hot driver
// shows up regardless of whether its work is in the ISR or the DPC.
//
// On a healthy system this view should show <5% of trace CPU.  Anything significantly
// higher (esp. from a non-Microsoft driver) is a candidate for "this hardware / driver is
// hogging CPU at high IRQL".  Consumer-grade GPU drivers, network drivers under heavy load,
// and AV-injected mini-drivers are frequent offenders.
//
// Requires the Interrupt + DPC kernel keywords in the capture profile.  Default WPR 'CPU'
// profiles enable them; check Capabilities.HasStackWalks for stack quality.
public static class InterruptStackAnalysis
{
    public static InterruptStacksResponse TopStacks(
        TraceLog trace,
        int top,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        int whenBuckets = 0)
    {
        var hasFilter = startUs.HasValue || endUs.HasValue;
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, whenBuckets);
        var ctx = BuildNormalized(trace, startUs, endUs, symbolLog, when);

        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = ctx.Normalized };
        var totalMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveMetric)
            .Take(top)
            .Select(n => new InterruptStackRow(
                Function: n.Name,
                ExclusiveUs: (long)n.ExclusiveMetric,
                InclusiveUs: (long)n.InclusiveMetric,
                ExclusiveCount: (long)n.ExclusiveCount,
                InclusiveCount: (long)n.InclusiveCount,
                ExclusivePct: 100.0 * n.ExclusiveMetric / totalMetric,
                InclusivePct: 100.0 * n.InclusiveMetric / totalMetric,
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, ctx.TraceTotalUs, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, ctx.TraceTotalUs, n.InclusiveMetric)))
            .ToList();

        return new InterruptStacksResponse(
            Rows: rows,
            TotalUs: ctx.TotalUs,
            DpcUs: ctx.DpcUs,
            IsrUs: ctx.IsrUs,
            TotalCount: ctx.TotalCount,
            Stats: ctx.Stats,
            Warnings: ctx.Warnings,
            When: when.Build());
    }

    public static CallerCalleeResponse CallerCallee(
        TraceLog trace,
        string focusFunction,
        int top,
        long? startUs,
        long? endUs,
        TextWriter symbolLog)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, 0);
        var ctx = BuildNormalized(trace, startUs, endUs, symbolLog, when);
        return StackSourceTopN.ComputeCallerCallee(
            ctx.Normalized, focusFunction, top, metricName: "interruptUs", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalUs,
        long TotalUs,
        long DpcUs,
        long IsrUs,
        long TotalCount,
        List<string> Warnings);

    private static BuildContext BuildNormalized(
        TraceLog trace,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        StackSourceTopN.WhenHistogram when)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(symbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalUs = 0;
        long totalUs = 0;
        long dpcUs = 0;
        long isrUs = 0;
        long totalCount = 0;

        // DPC and ISR events both carry ElapsedTimeMSec.  We convert to microseconds and
        // attribute the time to the routine's stack.  No PID filter — these events run in
        // kernel context (the "process" attribution is whichever thread happened to be
        // interrupted, which is misleading for diagnostics).
        void HandleDpc(DPCTraceData data)
        {
            var us = (long)(data.ElapsedTimeMSec * 1000);
            traceTotalUs += us;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (startUs is { } s && nowUs < s) return;
            if (endUs is { } e && nowUs > e) return;

            totalUs += us;
            dpcUs += us;
            totalCount++;
            raw.AddSample(data.CallStackIndex(), data, us);
            when.Add(nowUs, us);
        }

        void HandleIsr(ISRTraceData data)
        {
            var us = (long)(data.ElapsedTimeMSec * 1000);
            traceTotalUs += us;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (startUs is { } s && nowUs < s) return;
            if (endUs is { } e && nowUs > e) return;

            totalUs += us;
            isrUs += us;
            totalCount++;
            raw.AddSample(data.CallStackIndex(), data, us);
            when.Add(nowUs, us);
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.PerfInfoDPC += HandleDpc;
            kernel.PerfInfoISR += HandleIsr;
        });
        raw.Source.DoneAddingSamples();

        raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var warnings = new List<string>();
        if (totalCount == 0)
        {
            warnings.Add(
                "No DPC/ISR events matched. The capture profile likely omits the Interrupt or " +
                "DPC keyword (most WPR profiles include both, but custom .wprp may not).");
        }
        if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalUs, totalUs, dpcUs, isrUs, totalCount, warnings);
    }
}
