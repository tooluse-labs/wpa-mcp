using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class HeapTools
{
    private readonly TraceCache _cache;
    public HeapTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Top-N call stacks ranked by NT-heap allocation bytes (RtlAllocateHeap / HeapAlloc " +
        "/ malloc / new — anything that lands in the user-mode heap).  PerfView equivalent: " +
        "'HeapAllocStacks'.  The canonical native-leak-finder.  Distinct from " +
        "virtual_alloc_top_stacks: VirtualAlloc reserves page-granular address space; the " +
        "heap allocator sub-allocates from that.  Each row carries Exclusive/InclusiveBytes " +
        "and event counts; response also reports AllocBytes vs ReallocBytes.  Free events " +
        "carry no size on the wire and are NOT counted.  Requires the Heap kernel provider " +
        "enabled per-process (default WPR profiles do NOT enable it; use PerfView's " +
        "/HeapTrace flag or a custom .wprp <Heap> element).")]
    public HeapAllocStacksResponse HeapAllocTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N stacks by exclusive heap bytes (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Number of equal-width buckets for the time histogram (0 = disabled)")] int whenBuckets = 0,
        [Description(StackResponseOptions.CompactStacksDescription)]
        bool compactStacks = false,
        [Description(StackResponseOptions.SummaryOnlyDescription)]
        bool summaryOnly = false)
    {
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        return HeapAllocStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid, startUs, endUs, Console.Error, whenBuckets);
    }

    [McpServerTool, Description(
        "Caller-callee drill-down on a focus frame in the NT-heap allocation stack source.  " +
        "Metric is heap-allocation bytes; top-N callers ranked by inclusive bytes flowing " +
        "INTO focus, callees by bytes OUT.")]
    public CallerCalleeResponse HeapAllocCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus function name (substring or exact)")] string focusFunction,
        [Description("Top N callers / callees (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(focusFunction);
        var trace = _cache.Get(path);
        return HeapAllocStackAnalysis.CallerCallee(trace, focusFunction, top, pid, startUs, endUs, Console.Error);
    }
}
