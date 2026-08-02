using System.ComponentModel;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class InterruptTools
{
    private readonly TraceCache _cache;
    private readonly IPrivacyLogSink _privacyLog;
    public InterruptTools(TraceCache cache, IPrivacyLogSink? privacyLog = null)
    {
        _cache = cache;
        _privacyLog = privacyLog ?? PassThroughPrivacyLogSink.Instance;
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Top-N call stacks ranked by kernel interrupt time (DPC + ISR), in microseconds — " +
        "answers 'which driver routines are burning CPU at high IRQL'.  PerfView equivalent: " +
        "DPC/ISR Stacks.  ISR (Interrupt Service Routine) is the immediate kernel response to " +
        "hardware interrupts, runs at HIGH IRQL.  DPC (Deferred Procedure Call) is queued kernel " +
        "work that runs at DISPATCH_LEVEL.  Both are summed into a single 'interrupt time' " +
        "metric so a hot driver shows up regardless of where its work runs.  Response also " +
        "splits DpcUs / IsrUs. Interpret the share against a comparable workload and hardware " +
        "baseline; this tool does not impose a universal healthy threshold or infer driver fault. Requires Interrupt + " +
        "DPC keywords (default WPR 'CPU' profiles enable both).")]
    public InterruptStacksResponse InterruptTopStacks(
        [Description("Canonical TraceId returned by load_trace")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of interrupt μs over this many " +
                     "buckets across the filter window. Default 0 = histogram off.")]
        int whenBuckets = 0,
        [Description(StackResponseOptions.CompactStacksDescription)]
        bool compactStacks = false,
        [Description(StackResponseOptions.SummaryOnlyDescription)]
        bool summaryOnly = false,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return InterruptStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly),
            window.StartUs, window.EndUs, symbolLog: _privacyLog.Writer, whenBuckets: whenBuckets,
            filterSpecified: startUs.HasValue || endUs.HasValue);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Caller/callee drill-down for a focus function in the interrupt-stack data.  Metric is " +
        "interrupt time in microseconds; top-N callers ranked by inclusive μs flowing INTO " +
        "focus, callees by μs OUT.")]
    public CallerCalleeResponse InterruptCallerCallee(
        [Description("Canonical TraceId returned by load_trace")] string path,
        [Description("Focus frame name, exactly as it appears in interrupt_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return InterruptStackAnalysis.CallerCallee(
            trace, function, top, window.StartUs, window.EndUs, _privacyLog.Writer,
            filterSpecified: startUs.HasValue || endUs.HasValue);
    }
}
