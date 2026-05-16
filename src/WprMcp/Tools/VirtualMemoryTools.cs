using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class VirtualMemoryTools
{
    private readonly TraceCache _cache;
    public VirtualMemoryTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Top-N call stacks ranked by VirtualAlloc bytes — answers 'who's reserving / committing " +
        "virtual memory'.  PerfView equivalent: 'VirtualAlloc Stacks' view.  Counts both " +
        "VirtualMemAlloc and VirtualMemFree events; the metric is the allocation Length so " +
        "Free events show up with the size that was originally reserved.  Each row carries both " +
        "Bytes (metric=Length) and OpCount.  Requires the VirtualAlloc keyword in the capture " +
        "profile (default WPR 'CPU' / 'CPU.light' profiles do NOT enable it).")]
    public VirtualAllocStacksResponse VirtualAllocTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of allocation bytes over this many " +
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
        return VirtualAllocStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid, startUs, endUs, symbolLog: Console.Error, whenBuckets: whenBuckets);
    }

    [McpServerTool, Description(
        "Caller/callee drill-down for a focus function in the VirtualAlloc-stack data.  Metric " +
        "is allocation bytes; top-N callers ranked by inclusive bytes flowing INTO focus, callees " +
        "by bytes OUT.  PerfView equivalent: 'Callers' / 'Callees' tabs of VirtualAlloc Stacks.")]
    public CallerCalleeResponse VirtualAllocCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus frame name, exactly as it appears in virtual_alloc_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        return VirtualAllocStackAnalysis.CallerCallee(
            trace, function, top, pid, startUs, endUs, Console.Error);
    }
}
