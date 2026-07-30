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

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
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
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Optional thread ID; requires pid and is resolved within the requested half-open window.")]
        int? tid = null,
        [Description("Optional exact process start in trace-relative microseconds; requires pid. Without it, pid-only queries retain aggregate behavior across process lifetimes.")]
        long? processStartUs = null,
        [Description("Optional exact thread start in trace-relative microseconds; requires pid and tid.")]
        long? threadStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid, processStartUs, threadStartUs);
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        var scope = ThreadAnalysisScope.ResolveRequired(
            window, pid, tid, processStartUs, threadStartUs, TraceIdentityIndex.For(trace));
        return Analyzers.WaitAnalysis.Analyze(trace, top, scope);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Top-N call stacks ranked by blocked microseconds — answers 'where in the code is the wait " +
        "happening' (vs wait_analysis which answers 'which thread / which kernel wait reason'). Built " +
        "from the blocked thread's switch-out blocking stack on each ThreadCSwitch interval, " +
        "weighted by the exact blocked duration overlapping the requested window. " +
        "Mirrors PerfView's ThreadTimeStackComputer BlockedTime view. Requires the CSwitch keyword + " +
        "stack-walk-on-CSwitch in the capture profile.")]
    public WaitTopStacksResponse WaitTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of blocked μs over this many " +
                     "equal-width buckets across the filter window. Default 0 = histogram off. " +
                     "Use 20-30 to spot bursts vs steady-state inside a startup window.")]
        int whenBuckets = 0,
        [Description(StackResponseOptions.CompactStacksDescription)]
        bool compactStacks = false,
        [Description(StackResponseOptions.SummaryOnlyDescription)]
        bool summaryOnly = false,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false,
        [Description("Optional thread ID; requires pid and is resolved within the requested half-open window.")]
        int? tid = null,
        [Description("Optional exact process start in trace-relative microseconds; requires pid. Without it, pid-only queries retain aggregate behavior across process lifetimes.")]
        long? processStartUs = null,
        [Description("Optional exact thread start in trace-relative microseconds; requires pid and tid.")]
        long? threadStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid, processStartUs, threadStartUs);
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        var scope = ThreadAnalysisScope.ResolveRequired(
            window, pid, tid, processStartUs, threadStartUs, TraceIdentityIndex.For(trace));
        var filterSpecified = pid.HasValue || startUs.HasValue || endUs.HasValue ||
                              tid.HasValue || processStartUs.HasValue || threadStartUs.HasValue;
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return BlockedTimeStackAnalysis.TopBlockedStacks(
            trace,
            StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly),
            scope,
            symbolLog: Console.Error,
            whenBuckets: whenBuckets,
            filterSpecified: filterSpecified);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
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
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false,
        [Description("Optional thread ID; requires pid and is resolved within the requested half-open window.")]
        int? tid = null,
        [Description("Optional exact process start in trace-relative microseconds; requires pid. Without it, pid-only queries retain aggregate behavior across process lifetimes.")]
        long? processStartUs = null,
        [Description("Optional exact thread start in trace-relative microseconds; requires pid and tid.")]
        long? threadStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid, processStartUs, threadStartUs);
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        var scope = ThreadAnalysisScope.ResolveRequired(
            window, pid, tid, processStartUs, threadStartUs, TraceIdentityIndex.For(trace));
        var filterSpecified = pid.HasValue || startUs.HasValue || endUs.HasValue ||
                              tid.HasValue || processStartUs.HasValue || threadStartUs.HasValue;
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return BlockedTimeStackAnalysis.CallerCallee(
            trace, function, top, scope, Console.Error, filterSpecified);
    }
}
