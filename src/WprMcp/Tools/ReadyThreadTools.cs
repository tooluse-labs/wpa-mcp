using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class ReadyThreadTools
{
    private readonly TraceCache _cache;
    public ReadyThreadTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Top-N call stacks ranked by ReadyThread event count — answers 'who unblocked threads " +
        "in process X'.  PerfView equivalent: ReadyThread Stacks computer.  The stack on each " +
        "event is the READIER's stack (the code that did the SetEvent / ReleaseSemaphore / " +
        "IOCP completion / ALPC reply that woke the awaiting thread up), closing the " +
        "producer→consumer causality loop that wait_analysis only opens one side of.  Use this " +
        "with `awakenedPid` set to the PID you previously found high `BlockedUs` for in " +
        "`wait_analysis`.  Requires CSwitch / ReadyThread keywords (in default kernel profiles).")]
    public ReadyThreadStacksResponse ReadyThreadTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to events that readied threads in this process (the AWAKENED PID, " +
                     "not the readier).  Strongly recommended — without it, hot stacks are " +
                     "dominated by the kernel's IOCP / scheduler self-traffic.")]
        int? awakenedPid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of ready-event count over this many " +
                     "buckets across the filter window. Default 0 = histogram off.")]
        int whenBuckets = 0)
    {
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        return ReadyThreadStackAnalysis.TopStacks(
            trace, top, awakenedPid, startUs, endUs, symbolLog: Console.Error, whenBuckets: whenBuckets);
    }

    [McpServerTool, Description(
        "Caller/callee drill-down for a focus function in the ReadyThread-stack data.  Metric " +
        "is ready-event count; top-N callers ranked by inclusive count flowing INTO focus, " +
        "callees by count OUT.")]
    public CallerCalleeResponse ReadyThreadCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus frame name, exactly as it appears in ready_thread_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Filter to threads readied in this PID (same semantic as in top_stacks).")]
        int? awakenedPid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        return ReadyThreadStackAnalysis.CallerCallee(
            trace, function, top, awakenedPid, startUs, endUs, Console.Error);
    }
}
