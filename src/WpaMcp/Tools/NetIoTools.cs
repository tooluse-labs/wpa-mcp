using System.ComponentModel;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class NetIoTools
{
    private readonly TraceCache _cache;
    private readonly IPrivacyLogSink _privacyLog;
    public NetIoTools(TraceCache cache, IPrivacyLogSink? privacyLog = null)
    {
        _cache = cache;
        _privacyLog = privacyLog ?? PassThroughPrivacyLogSink.Instance;
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Top-N call stacks ranked by network bytes (TCP + UDP, send + receive, IPv4 + IPv6) — " +
        "answers 'which call chain is doing all the network IO'.  PerfView equivalent: " +
        "'TCP/IP Stacks' + 'UDP/IP Stacks' merged.  Distinguishes 'one socket streaming a big " +
        "file' from 'thousands of small RPC round-trips'.  Response also reports TcpBytes / " +
        "UdpBytes split so consumers can tell whether the workload is mostly TCP or UDP without " +
        "re-aggregating.  Connect/Accept/Disconnect events are NOT counted (no byte metric); " +
        "use find_marker for those.  Requires the NetworkTrace keyword in the capture profile " +
        "(default WPR 'CPU' / 'CPU.light' profiles do NOT enable it).")]
    public NetIoStacksResponse NetTopStacks(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of network bytes over this many " +
                     "buckets across the filter window. Default 0 = histogram off.")]
        int whenBuckets = 0,
        [Description(StackResponseOptions.CompactStacksDescription)]
        bool compactStacks = false,
        [Description(StackResponseOptions.SummaryOnlyDescription)]
        bool summaryOnly = false,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false,
        [Description("Optional process lifetime start in microseconds; requires pid. PID-only queries explicitly aggregate reused lifetimes.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid: null, processStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        using var traceLease = _cache.Acquire(traceId);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return NetIoStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid,
            window.StartUs, window.EndUs, symbolLog: _privacyLog.Writer, whenBuckets: whenBuckets,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue,
            processStartUs: processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Caller/callee drill-down for a focus function in the network-stack data.  Metric is " +
        "network bytes (send + receive, TCP + UDP, IPv4 + IPv6); top-N callers ranked by " +
        "inclusive bytes flowing INTO focus, callees by bytes OUT.")]
    public CallerCalleeResponse NetCallerCallee(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Focus frame name, exactly as it appears in net_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false,
        [Description("Optional process lifetime start in microseconds; requires pid. PID-only queries explicitly aggregate reused lifetimes.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid: null, processStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        using var traceLease = _cache.Acquire(traceId);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return NetIoStackAnalysis.CallerCallee(
            trace, function, top, pid, window.StartUs, window.EndUs, _privacyLog.Writer,
            processStartUs,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue);
    }

    [McpServerTool(
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false), Description(
        "Per-connection TCP lifecycle list — Connect/Accept paired with Disconnect/Reconnect " +
        "by `connid` to give 'connection X opened at T1, closed at T2, lasted T2−T1'.  " +
        "Useful for finding unusually long observed connection lifecycles. This duration is " +
        "not connection-establishment latency, request/response latency, or network RTT, so it " +
        "cannot by itself attribute a slow RPC to setup. Role: connect = outbound, accept = inbound. IPv4 + IPv6 merged " +
        "into one list with an IsIPv6 flag.  Connections still open when capture stopped have " +
        "TraceResidentEnd=true with null CloseTimeUs and DurationUs. Pairing uses the emitter's " +
        "process lifetime plus connid, so PID/connid reuse cannot cross-pair sessions. A second open on the same " +
        "slot marks the prior row replaced_open_unobserved with null CloseTimeUs/DurationUs; its timestamp is not " +
        "invented as a close. Reconnect on a connid is " +
        "treated as the prior session ending. ConnIdText is the authoritative exact unsigned-decimal " +
        "identifier; the deprecated numeric ConnId is null above JavaScript's safe-integer limit, as " +
        "reported by ConnIdLegacyStatus. " +
        "An in-scope Disconnect/Reconnect without a preceding " +
        "open increments UnpairedCloseCount and returns NoDataReason=unpaired_endpoints_in_scope " +
        "when no lifecycle can be projected; it is not mislabeled as no events. Requires the NetworkTrace keyword in the " +
        "capture profile (default WPR 'CPU' / 'CPU.light' profiles do NOT enable it).")]
    public NetConnectionsResponse NetConnections(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Top N connections: observed durations descending, then unobserved/null durations last; stable ties use open time, PID, process start, and exact connection ID (default 100, max 1000)")] int top = 100,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start; connections whose lifecycle intersects the window are included")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive); connections whose lifecycle intersects the window are included")] long? endUs = null,
        [Description("Optional exact process start in trace-relative microseconds; requires pid. Without it, pid-only queries explicitly aggregate intersecting lifetimes while rows remain separated by ProcessStartUs.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(
            pid, tid: null, processStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        using var traceLease = _cache.Acquire(traceId);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        return NetConnectionAnalysis.Analyze(
            trace, pid, top, window.StartUs, window.EndUs, processStartUs);
    }
}
