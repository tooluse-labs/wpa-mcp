using Microsoft.Diagnostics.Tracing;
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
        int whenBuckets = 0,
        bool? filterSpecified = null)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, whenBuckets);
        // Pid: null — interrupt events run in kernel context, no per-process attribution.
        var req = new StackAnalysisRequest(Pid: null, startUs, endUs, symbolLog, when)
        {
            FilterSpecified = filterSpecified,
        };
        var ctx = BuildNormalized(trace, req);

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
                ExclusivePct: StackSourceTopN.Pct(totalMetric, n.ExclusiveMetric),
                InclusivePct: StackSourceTopN.Pct(totalMetric, n.InclusiveMetric),
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalUs, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalUs, n.InclusiveMetric)))
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
        var req = new StackAnalysisRequest(Pid: null, startUs, endUs, symbolLog, when);
        var ctx = BuildNormalized(trace, req);
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

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalUs = 0;
        long totalUs = 0;
        long dpcUs = 0;
        long isrUs = 0;
        long totalCount = 0;
        long noStackCount = 0;
        long noStackUs = 0;

        // No PID filter — these events run in kernel context; "process" attribution is
        // whichever thread happened to be interrupted, which is misleading for diagnostics.
        void HandleDpc(DPCTraceData data)
        {
            var us = (long)(data.ElapsedTimeMSec * 1000);
            traceTotalUs += us;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (!req.PassesFilter(nowUs)) return;

            totalUs += us;
            dpcUs += us;
            totalCount++;
            if (data.CallStackIndex() == CallStackIndex.Invalid)
            {
                noStackCount++;
                noStackUs += us;
            }
            raw.AddSample(data.CallStackIndex(), data, us);
            req.When.Add(nowUs, us);
        }

        void HandleIsr(ISRTraceData data)
        {
            var us = (long)(data.ElapsedTimeMSec * 1000);
            traceTotalUs += us;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (!req.PassesFilter(nowUs)) return;

            totalUs += us;
            isrUs += us;
            totalCount++;
            if (data.CallStackIndex() == CallStackIndex.Invalid)
            {
                noStackCount++;
                noStackUs += us;
            }
            raw.AddSample(data.CallStackIndex(), data, us);
            req.When.Add(nowUs, us);
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.PerfInfoDPC += HandleDpc;
            kernel.PerfInfoISR += HandleIsr;
        });
        raw.Source.DoneAddingSamples();

        if (req.ResolveSymbols)
            raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var warnings = new List<string>();
        if (totalCount == 0)
            warnings.Add(WarningBuilder.NoEventsInDefaultProfile("DPC/ISR", "Interrupt + DPC"));
        // Warn only when missing stacks dominate the metric being ranked: interrupt time.
        // A few long no-stack DPC/ISR events can matter more than many short stacked events.
        else if (ShouldWarnMissingStacks(noStackUs, totalUs))
            warnings.Add(WarningBuilder.MissingInterruptStacks(noStackCount, totalCount, noStackUs, totalUs));
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalUs, totalUs, dpcUs, isrUs, totalCount, warnings);
    }

    internal static bool ShouldWarnMissingStacks(long noStackUs, long totalUs)
        => totalUs > 0 && noStackUs / (double)totalUs >= 0.5;
}
