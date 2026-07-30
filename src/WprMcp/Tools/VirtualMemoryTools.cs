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

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Process memory resource snapshots from Memory/ProcessMemInfo plus observed handle " +
        "create/close deltas. Reports working set, commit, derived private bytes, private " +
        "working set, virtual size, handle deltas, observed pool allocation/free deltas, and " +
        "a memory-pressure summary with minimum free bytes, minimum free+zero bytes when " +
        "zero-page data is present, plus peak observed sampled-process batch totals across " +
        "the selected window. These pressure totals are ETW sample-batch evidence, not " +
        "complete whole-system memory accounting. " +
        "Pool rows are not absolute current counters; they are captured-window deltas with " +
        "UnknownFreeCount for frees whose allocation was outside the window. Requires MemoryInfoWS " +
        "for process snapshots and Handle/Pool for handle or pool events; use MemoryCapture.wprp " +
        "when resident footprint, handle-leak, or pool-growth questions matter. " +
        "Process rows are ordered by current working set bytes; handle rows by absolute net " +
        "delta. Neither order implies severity or causality.")]
    public MemoryResourceResponse MemoryResourceAnalysis(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N process and handle rows (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequirePidTid(pid, tid: null);
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        return Analyzers.MemoryResourceAnalysis.Analyze(trace, top, pid, window.StartUs, window.EndUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
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
        bool summaryOnly = false,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequirePidTid(pid, tid: null);
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return VirtualAllocStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid,
            window.StartUs, window.EndUs, symbolLog: Console.Error, whenBuckets: whenBuckets,
            filterSpecified: pid.HasValue || startUs.HasValue || endUs.HasValue);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
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
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequirePidTid(pid, tid: null);
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return VirtualAllocStackAnalysis.CallerCallee(
            trace, function, top, pid, window.StartUs, window.EndUs, Console.Error);
    }
}
