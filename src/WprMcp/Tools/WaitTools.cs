using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class WaitTools
{
    private readonly TraceCache _cache;
    public WaitTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Per-thread blocked-time analysis — the canonical 'why was this slow' answer when CPU " +
        "usage is low and wall-clock is high.  PerfView equivalent: 'Thread Time' view, " +
        "blocked-time aggregated per thread.  Built from ThreadCSwitch wait→resume intervals: " +
        "for each thread, sums the time it sat off-CPU between switch-out and switch-in.  Each " +
        "row carries dominant kernel wait reasons (WrFilterContext = blocked in a Filter " +
        "Manager minifilter callback, WrUserRequest = WaitForSingleObject, WrLpcReceive = " +
        "ALPC reply, etc.) which directly identify the kernel state.  Pair with wait_top_stacks " +
        "to find the call chain (this answers 'which thread / which reason'; that one answers " +
        "'where in the code').  Requires the CSwitch keyword (default WPR 'CPU' profiles do).")]
    public WaitAnalysisResponse WaitAnalysis(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        return Analyzers.WaitAnalysis.Analyze(trace, top, pid, startUs, endUs);
    }

    [McpServerTool, Description(
        "Top-N call stacks ranked by blocked microseconds — answers 'where in the code is the wait " +
        "happening' (vs wait_analysis which answers 'which thread / which kernel wait reason'). Built " +
        "from the resume-point stack walk on each ThreadCSwitch event, weighted by blocked time. " +
        "Mirrors PerfView's ThreadTimeStackComputer BlockedTime view. Requires the CSwitch keyword + " +
        "stack-walk-on-CSwitch in the capture profile.")]
    public WaitTopStacksResponse WaitTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of blocked μs over this many " +
                     "equal-width buckets across the filter window. Default 0 = histogram off. " +
                     "Use 20-30 to spot bursts vs steady-state inside a startup window.")]
        int whenBuckets = 0,
        [Description(StackResponseOptions.CompactStacksDescription)]
        bool compactStacks = false,
        [Description(StackResponseOptions.SummaryOnlyDescription)]
        bool summaryOnly = false)
    {
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        return BlockedTimeStackAnalysis.TopBlockedStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid, startUs, endUs, symbolLog: Console.Error, whenBuckets: whenBuckets);
    }

    [McpServerTool, Description(
        "Caller/callee drill-down for a focus function in the wait-stack data. PerfView " +
        "equivalent: 'Callers' / 'Callees' tabs of Thread Time / Wait Time view. Metric is " +
        "blocked microseconds; top-N callers ranked by inclusive blocked μs flowing INTO focus, " +
        "callees ranked by μs flowing OUT to them.")]
    public CallerCalleeResponse WaitCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus frame name, exactly as it appears in wait_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        return BlockedTimeStackAnalysis.CallerCallee(
            trace, function, top, pid, startUs, endUs, Console.Error);
    }
}
