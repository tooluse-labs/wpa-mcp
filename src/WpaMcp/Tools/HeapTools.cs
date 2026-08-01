using System.ComponentModel;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class HeapTools
{
    private readonly TraceCache _cache;
    public HeapTools(TraceCache cache) => _cache = cache;

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Top-N call stacks ranked by NT-heap allocation bytes (RtlAllocateHeap / HeapAlloc " +
        "/ malloc / new — anything that lands in the user-mode heap).  PerfView equivalent: " +
        "'HeapAllocStacks'. This is an observed allocation-flow view, not proof of retained " +
        "objects or a native leak because free events do not carry sizes. Distinct from " +
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
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return HeapAllocStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid,
            window.StartUs, window.EndUs, Console.Error, whenBuckets,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue,
            processStartUs: processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Caller-callee drill-down on a focus frame in the NT-heap allocation stack source.  " +
        "Metric is heap-allocation bytes; top-N callers ranked by inclusive bytes flowing " +
        "INTO focus, callees by bytes OUT.")]
    public CallerCalleeResponse HeapAllocCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus function name (substring or exact)")] string focusFunction,
        [Description("Top N callers / callees (default 20, max 1000)")] int top = 20,
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
        Validation.RequireFunctionName(focusFunction);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return HeapAllocStackAnalysis.CallerCallee(
            trace, focusFunction, top, pid, window.StartUs, window.EndUs, Console.Error,
            processStartUs,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue);
    }
}
