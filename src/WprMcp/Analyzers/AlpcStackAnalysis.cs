using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by ALPC (Async Local Procedure Call) message count.  ALPC is the
// kernel IPC primitive used by RPC, COM, AppContainer broker calls, lsass, the SCM,
// and most of the Windows service surface.  Useful for "which call chain is doing all
// the cross-process IPC" / "is this slow because of an LPC round-trip" questions.
//
// We count Send + Receive events.  Wait/Unwait events fire when a thread blocks/unblocks
// waiting for a reply — those are essentially CSwitch / ReadyThread duplicates and would
// double-count, so we skip them here (they're available individually via find_marker).
//
// Metric = 1 per message.  No natural byte metric — ALPC payload size isn't on the event.
//
// Requires the ALPC keyword in the capture profile.  Default WPR 'CPU' / 'CPU.light'
// profiles do NOT enable it; 'GeneralProfile' or a custom .wprp with the ALPC keyword does.
public static class AlpcStackAnalysis
{
    public static AlpcStacksResponse TopStacks(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        int whenBuckets = 0,
        bool? filterSpecified = null)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, whenBuckets);
        var req = new StackAnalysisRequest(pid, startUs, endUs, symbolLog, when)
        {
            FilterSpecified = filterSpecified,
        };
        var ctx = BuildNormalized(trace, req);

        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = ctx.Normalized };
        var totalMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveMetric)
            .Take(top)
            .Select(n => new AlpcStackRow(
                Function: n.Name,
                ExclusiveEvents: (long)n.ExclusiveMetric,
                InclusiveEvents: (long)n.InclusiveMetric,
                ExclusivePct: StackSourceTopN.Pct(totalMetric, n.ExclusiveMetric),
                InclusivePct: StackSourceTopN.Pct(totalMetric, n.InclusiveMetric),
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalCount, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalCount, n.InclusiveMetric)))
            .ToList();

        return new AlpcStacksResponse(
            Rows: rows,
            TotalEvents: ctx.TotalCount,
            SendCount: ctx.SendCount,
            ReceiveCount: ctx.ReceiveCount,
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
            ctx.Normalized, focusFunction, top, metricName: "alpcEvents", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalCount,
        long TotalCount,
        long SendCount,
        long ReceiveCount,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalCount = 0;
        long totalCount = 0;
        long sendCount = 0;
        long receiveCount = 0;

        void Sample(int processId, double tsRelMs, Microsoft.Diagnostics.Tracing.TraceEvent ev, bool isSend)
        {
            traceTotalCount++;
            var nowUs = (long)(tsRelMs * 1000);
            if (!req.PassesFilter(processId, nowUs)) return;

            totalCount++;
            if (isSend) sendCount++;
            else receiveCount++;
            raw.AddSample(ev.CallStackIndex(), ev, 1);
            req.When.Add(nowUs, 1);
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ALPCSendMessage    += d => Sample(d.ProcessID, d.TimeStampRelativeMSec, d, isSend: true);
            kernel.ALPCReceiveMessage += d => Sample(d.ProcessID, d.TimeStampRelativeMSec, d, isSend: false);
        });
        raw.Source.DoneAddingSamples();

        if (req.ResolveSymbols)
            raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var warnings = new List<string>();
        if (totalCount == 0)
            warnings.Add(WarningBuilder.MissingKeyword("ALPC Send/Receive", "ALPC"));
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalCount, totalCount, sendCount, receiveCount, warnings);
    }
}
