using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// Top stacks ranked by network bytes — PerfView's "TCP/IP Stacks" + "UDP/IP Stacks" view,
// merged.  Reports the call chains that drove TCP and UDP send/receive activity.  Useful
// for "who's doing all the network IO" / "what code path is hammering this socket"
// questions, and for diagnosing "high wall, low CPU" where the wait is on a network
// round-trip rather than disk or CPU.
//
// Sample weight = bytes per send/recv (from TcpIp/UdpIp data's `size` field).  CallTree.
// ExclusiveMetric reads as "exclusive bytes sent/received by this frame"; ExclusiveCount
// tracks the operation count on the same stack source.
//
// Includes both IPv4 (TcpIpSend/Recv) and IPv6 (TcpIpSendIPV6/RecvIPV6) variants, and TCP
// + UDP equivalents.  Connect / Accept / Disconnect events are NOT counted here (they have
// no byte metric) — those are exposed via find_marker.
//
// Requires the NetworkTrace kernel keyword in the capture profile.  Default WPR 'CPU' /
// 'CPU.light' profiles do NOT enable it; 'GeneralProfile' or a custom .wprp does.
public static class NetIoStackAnalysis
{
    public static NetIoStacksResponse TopStacks(
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
        var totalBytesMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
            .Where(_ => ctx.StackCoverage.TotalEventCount > 0)
            .OrderByDescending(n => n.ExclusiveMetric)
            .Take(top)
            .Select(n => new NetIoStackRow(
                Function: n.Name,
                ExclusiveBytes: (long)n.ExclusiveMetric,
                InclusiveBytes: (long)n.InclusiveMetric,
                ExclusiveOpCount: (long)n.ExclusiveCount,
                InclusiveOpCount: (long)n.InclusiveCount,
                ExclusivePct: StackSourceTopN.Pct(totalBytesMetric, n.ExclusiveMetric),
                InclusivePct: StackSourceTopN.Pct(totalBytesMetric, n.InclusiveMetric),
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalBytes, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalBytes, n.InclusiveMetric)))
            .ToList();

        return new NetIoStacksResponse(
            Rows: rows,
            TotalBytes: ctx.TotalBytes,
            TotalOpCount: ctx.TotalOps,
            TcpBytes: ctx.TcpBytes,
            UdpBytes: ctx.UdpBytes,
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
            ctx.Normalized, focusFunction, top, metricName: "netBytes", ctx.Stats, ctx.Warnings,
            sourceTotalMetric: ctx.TotalBytes,
            stackCoverage: ctx.StackCoverage,
            resultContract: contract);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalBytes,
        long TraceEventCount,
        long TotalBytes,
        long TotalOps,
        long TcpBytes,
        long UdpBytes,
        DomainStackCoverage StackCoverage,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(trace, req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace, "net_io", "bytes");
        long traceTotalBytes = 0;
        long traceEventCount = 0;
        long totalBytes = 0;
        long totalOps = 0;
        long tcpBytes = 0;
        long udpBytes = 0;

        void Sample(int processId, int size, double tsRelMs, Microsoft.Diagnostics.Tracing.TraceEvent ev, bool isTcp)
        {
            var bytes = (long)size;
            traceTotalBytes += bytes;
            var nowUs = (long)(tsRelMs * 1000);
            if (req.PassesFilter(nowUs)) traceEventCount++;
            if (!req.PassesFilter(processId, nowUs)) return;

            totalBytes += bytes;
            totalOps++;
            if (isTcp) tcpBytes += bytes;
            else udpBytes += bytes;
            raw.AddSample(ev.CallStackIndex(), ev, bytes);
            req.When.Add(nowUs, bytes);
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.TcpIpSend       += d => Sample(d.ProcessID, d.size, d.TimeStampRelativeMSec, d, isTcp: true);
            kernel.TcpIpRecv       += d => Sample(d.ProcessID, d.size, d.TimeStampRelativeMSec, d, isTcp: true);
            kernel.TcpIpSendIPV6   += d => Sample(d.ProcessID, d.size, d.TimeStampRelativeMSec, d, isTcp: true);
            kernel.TcpIpRecvIPV6   += d => Sample(d.ProcessID, d.size, d.TimeStampRelativeMSec, d, isTcp: true);
            kernel.UdpIpSend       += d => Sample(d.ProcessID, d.size, d.TimeStampRelativeMSec, d, isTcp: false);
            kernel.UdpIpRecv       += d => Sample(d.ProcessID, d.size, d.TimeStampRelativeMSec, d, isTcp: false);
            kernel.UdpIpSendIPV6   += d => Sample(d.ProcessID, d.size, d.TimeStampRelativeMSec, d, isTcp: false);
            kernel.UdpIpRecvIPV6   += d => Sample(d.ProcessID, d.size, d.TimeStampRelativeMSec, d, isTcp: false);
        });
        raw.Source.DoneAddingSamples();

        var lookupAttempt = StackSourceTopN.TryLookupWarmSymbols(
            raw.Source, req.ResolveSymbols, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw, lookupAttempt);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);
        var coverage = raw.Coverage.Snapshot();

        var warnings = new List<string>();
        if (totalOps == 0 && !req.HasFilter)
            warnings.Add(WarningBuilder.MissingKeyword("TcpIp/UdpIp send/recv", "NetworkTrace"));
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate is { } resolutionRate && resolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(resolutionRate));
        StackSourceTopN.AddCoverageWarning(warnings, coverage);
        StackSourceTopN.AddSymbolLookupWarning(warnings, stats);

        return new BuildContext(normalized, stats, traceTotalBytes, traceEventCount, totalBytes, totalOps, tcpBytes, udpBytes, coverage, warnings);
    }
}
