using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class MmapTools
{
    private readonly TraceCache _cache;
    public MmapTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Top N memory-mapped files by hard page-in bytes. " +
        "Identifies which mmap'd file caused page-in load (e.g., a network-share PDF).")]
    public MmapHotFilesResponse MmapHotFiles(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        return MmapAnalysis.HotFiles(trace, top, pid);
    }

    [McpServerTool, Description(
        "Top-N call stacks ranked by hard-fault paging-in bytes — answers 'which call chain " +
        "is paging in cold pages from disk'. PerfView equivalent: 'Memory Hard Fault Stacks' " +
        "view. Pairs with mmap_hot_files (per-file bucket); this one is per-stack so you can " +
        "tell eager loader resolution apart from lazy use of a constructor or scanner-induced " +
        "page-in. Each row carries both PageInBytes (metric=ByteCount) and FaultCount. Requires " +
        "the HardFaults kernel keyword in the capture profile.")]
    public HardFaultStacksResponse MmapTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of paging-in bytes over this many " +
                     "buckets across the filter window. Default 0 = histogram off.")]
        int whenBuckets = 0)
    {
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        return PageFaultStackAnalysis.TopFaultStacks(
            trace, top, pid, startUs, endUs, symbolLog: Console.Error, whenBuckets: whenBuckets);
    }

    [McpServerTool, Description(
        "Caller/callee drill-down for a focus function in the hard-fault-stack data. Metric " +
        "is page-in bytes; top-N callers ranked by inclusive bytes paged in for focus, callees " +
        "by bytes flowing through. Requires HardFaults keyword in the capture profile.")]
    public CallerCalleeResponse MmapCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus frame name, exactly as it appears in mmap_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        return PageFaultStackAnalysis.CallerCallee(
            trace, function, top, pid, startUs, endUs, Console.Error);
    }
}
