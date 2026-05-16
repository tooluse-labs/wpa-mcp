using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class InterruptTools
{
    private readonly TraceCache _cache;
    public InterruptTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Top-N call stacks ranked by kernel interrupt time (DPC + ISR), in microseconds — " +
        "answers 'which driver routines are burning CPU at high IRQL'.  PerfView equivalent: " +
        "DPC/ISR Stacks.  ISR (Interrupt Service Routine) is the immediate kernel response to " +
        "hardware interrupts, runs at HIGH IRQL.  DPC (Deferred Procedure Call) is queued kernel " +
        "work that runs at DISPATCH_LEVEL.  Both are summed into a single 'interrupt time' " +
        "metric so a hot driver shows up regardless of where its work runs.  Response also " +
        "splits DpcUs / IsrUs.  On a healthy system this should be <5% of trace CPU time; " +
        "more than that, esp. from non-Microsoft drivers, is a red flag.  Requires Interrupt + " +
        "DPC keywords (default WPR 'CPU' profiles enable both).")]
    public InterruptStacksResponse InterruptTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of interrupt μs over this many " +
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
        return InterruptStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), startUs, endUs, symbolLog: Console.Error, whenBuckets: whenBuckets);
    }

    [McpServerTool, Description(
        "Caller/callee drill-down for a focus function in the interrupt-stack data.  Metric is " +
        "interrupt time in microseconds; top-N callers ranked by inclusive μs flowing INTO " +
        "focus, callees by μs OUT.")]
    public CallerCalleeResponse InterruptCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus frame name, exactly as it appears in interrupt_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        return InterruptStackAnalysis.CallerCallee(
            trace, function, top, startUs, endUs, Console.Error);
    }
}
