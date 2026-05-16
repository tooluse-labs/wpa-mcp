using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class NetIoTools
{
    private readonly TraceCache _cache;
    public NetIoTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Top-N call stacks ranked by network bytes (TCP + UDP, send + receive, IPv4 + IPv6) — " +
        "answers 'which call chain is doing all the network IO'.  PerfView equivalent: " +
        "'TCP/IP Stacks' + 'UDP/IP Stacks' merged.  Distinguishes 'one socket streaming a big " +
        "file' from 'thousands of small RPC round-trips'.  Response also reports TcpBytes / " +
        "UdpBytes split so consumers can tell whether the workload is mostly TCP or UDP without " +
        "re-aggregating.  Connect/Accept/Disconnect events are NOT counted (no byte metric); " +
        "use find_marker for those.  Requires the NetworkTrace keyword in the capture profile " +
        "(default WPR 'CPU' / 'CPU.light' profiles do NOT enable it).")]
    public NetIoStacksResponse NetTopStacks(
        [Description("Absolute path to .etl file")] string path,
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
        bool summaryOnly = false)
    {
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        return NetIoStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid, startUs, endUs, symbolLog: Console.Error, whenBuckets: whenBuckets);
    }

    [McpServerTool, Description(
        "Caller/callee drill-down for a focus function in the network-stack data.  Metric is " +
        "network bytes (send + receive, TCP + UDP, IPv4 + IPv6); top-N callers ranked by " +
        "inclusive bytes flowing INTO focus, callees by bytes OUT.")]
    public CallerCalleeResponse NetCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus frame name, exactly as it appears in net_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        return NetIoStackAnalysis.CallerCallee(
            trace, function, top, pid, startUs, endUs, Console.Error);
    }

    [McpServerTool, Description(
        "Per-connection TCP lifecycle list — Connect/Accept paired with Disconnect/Reconnect " +
        "by `connid` to give 'connection X opened at T1, closed at T2, lasted T2−T1'.  " +
        "Useful for 'connect-to-disconnect latency outliers' / 'is RPC slow because of " +
        "connection setup'.  Role: connect = outbound, accept = inbound.  IPv4 + IPv6 merged " +
        "into one list with an IsIPv6 flag.  Connections still open when capture stopped have " +
        "TraceResidentEnd=true and CloseTimeUs at trace end.  Reconnect on a connid is " +
        "treated as the prior session ending.  Requires the NetworkTrace keyword in the " +
        "capture profile (default WPR 'CPU' / 'CPU.light' profiles do NOT enable it).")]
    public NetConnectionsResponse NetConnections(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N connections by duration descending (default 100, max 1000)")] int top = 100,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start (filter on connection open time)")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        return NetConnectionAnalysis.Analyze(trace, pid, top, startUs, endUs);
    }
}
