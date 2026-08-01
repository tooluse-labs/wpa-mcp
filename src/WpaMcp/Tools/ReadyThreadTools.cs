using System.ComponentModel;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class ReadyThreadTools
{
    private readonly TraceCache _cache;
    public ReadyThreadTools(TraceCache cache) => _cache = cache;

    [McpServerTool(ReadOnly = false, Idempotent = true, OpenWorld = true, Destructive = true), Description(
        "Top-N associated readier/wakeup stack evidence ranked by ReadyThread event count. " +
        "Events are aggregated by optional `awakenedPid` and requested window; the stack belongs " +
        "to the readier, not the awakened thread. Results are not paired one-to-one with a " +
        "specific wait interval or subsequent CSwitch and cannot alone establish root cause. " +
        "Use after `wait_analysis` as supporting evidence. Requires CSwitch / ReadyThread events.")]
    public ReadyThreadStacksResponse ReadyThreadTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Aggregate events for threads in this awakened PID, not the readier PID. " +
                     "This scope does not identify a specific wait interval.")]
        int? awakenedPid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of ready-event count over this many " +
                     "buckets across the filter window. Default 0 = histogram off.")]
        int whenBuckets = 0,
        [Description(StackResponseOptions.CompactStacksDescription)]
        bool compactStacks = false,
        [Description(StackResponseOptions.SummaryOnlyDescription)]
        bool summaryOnly = false,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false,
        [Description("Optional awakened-process lifetime start in microseconds; requires awakenedPid. PID-only queries explicitly aggregate reused lifetimes.")]
        long? awakenedProcessStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(
            awakenedPid, tid: null, awakenedProcessStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return ReadyThreadStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), awakenedPid,
            window.StartUs, window.EndUs, symbolLog: Console.Error, whenBuckets: whenBuckets,
            filterSpecified: awakenedPid.HasValue || awakenedProcessStartUs.HasValue || startUs.HasValue || endUs.HasValue,
            awakenedProcessStartUs: awakenedProcessStartUs);
    }

    [McpServerTool(ReadOnly = false, Idempotent = true, OpenWorld = true, Destructive = true), Description(
        "Caller/callee drill-down for associated readier/wakeup stack evidence around a focus " +
        "function. Metric is ReadyThread event count, aggregated by optional `awakenedPid` and " +
        "requested window. Results are not paired one-to-one with a specific wait interval or " +
        "subsequent CSwitch and cannot alone establish root cause.")]
    public CallerCalleeResponse ReadyThreadCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus frame name, exactly as it appears in ready_thread_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Filter to threads readied in this PID (same semantic as in top_stacks).")]
        int? awakenedPid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false,
        [Description("Optional awakened-process lifetime start in microseconds; requires awakenedPid. PID-only queries explicitly aggregate reused lifetimes.")]
        long? awakenedProcessStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(
            awakenedPid, tid: null, awakenedProcessStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return ReadyThreadStackAnalysis.CallerCallee(
            trace, function, top, awakenedPid, window.StartUs, window.EndUs, Console.Error,
            awakenedProcessStartUs,
            filterSpecified: awakenedPid.HasValue || awakenedProcessStartUs.HasValue || startUs.HasValue || endUs.HasValue);
    }
}
