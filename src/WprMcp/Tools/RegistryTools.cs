using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class RegistryTools
{
    private readonly TraceCache _cache;
    public RegistryTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Top-N call stacks ranked by registry-operation count — answers 'who's pounding the " +
        "registry' / 'where do these lookups come from'.  PerfView equivalent: 'Registry Stacks' " +
        "view.  Counts Query, Open, Create, SetValue, Delete, Enumerate, Virtualize events; " +
        "skips housekeeping (KCB rundown, Flush, Close).  Each row is per-stack ops (no byte " +
        "metric — registry ops don't have a natural byte cost).  Requires the Registry keyword " +
        "in the capture profile (default WPR 'CPU' / 'CPU.light' profiles do NOT enable it).")]
    public RegistryStacksResponse RegistryTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of operation count over this many " +
                     "buckets across the filter window. Default 0 = histogram off.")]
        int whenBuckets = 0)
    {
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        return RegistryStackAnalysis.TopStacks(
            trace, top, pid, startUs, endUs, symbolLog: Console.Error, whenBuckets: whenBuckets);
    }

    [McpServerTool, Description(
        "Caller/callee drill-down for a focus function in the registry-stack data.  Metric is " +
        "operation count; top-N callers ranked by inclusive ops flowing INTO focus, callees " +
        "by ops OUT.")]
    public CallerCalleeResponse RegistryCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus frame name, exactly as it appears in registry_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        return RegistryStackAnalysis.CallerCallee(
            trace, function, top, pid, startUs, endUs, Console.Error);
    }
}
