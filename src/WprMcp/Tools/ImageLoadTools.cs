using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class ImageLoadTools
{
    private readonly TraceCache _cache;
    public ImageLoadTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Per-process DLL/image-load timeline in chronological order — every ImageLoad event " +
        "with absolute timestamp, offset from ProcessStart, and gap from the previous load.  " +
        "PerfView equivalent: filter the 'Events' view to ImageLoad for one PID (no native " +
        "composite view).  Use to spot late-loading DLLs, unusually long inter-load gaps that " +
        "hint at minifilter / sig-scan delays, or a single DLL that took a long time to map.  " +
        "Pair with image_load_top_gaps (same data ranked by gap, with FirstLoadOffsetUs) and " +
        "image_load_top_stacks (the call chain that triggered each load).  For load *durations* " +
        "(not gaps between loads), combine with wait_analysis on the PID's main thread.  " +
        "Requires the Loader keyword (default WPR profiles include it).")]
    public ImageLoadTimingResponse ImageLoadTiming(
        [Description("Absolute path to .etl file")] string path,
        [Description("Process ID")] int pid,
        [Description("Top N loads (default 100, max 1000)")] int top = 100)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        return ImageLoadAnalysis.PerProcess(trace, pid, top);
    }

    [McpServerTool, Description(
        "Top-N image loads with the LARGEST gap from the previous load (chronological). Use to " +
        "spot 'loader was frozen for ~Xms between DLL Y and DLL Z' patterns that hint at per-DLL " +
        "minifilter scans / signature checks. Response also carries FirstLoadOffsetUs (kernel-side " +
        "gap before any DLL loaded — process-creation-callback time). Pairs with image_load_timing " +
        "(chronological list) — same data, different ordering.")]
    public ImageLoadTopGapsResponse ImageLoadTopGaps(
        [Description("Absolute path to .etl file")] string path,
        [Description("Process ID")] int pid,
        [Description("Top N gap rows (default 20, max 1000)")] int top = 20)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        return ImageLoadAnalysis.TopGaps(trace, pid, top);
    }

    [McpServerTool, Description(
        "Top-N call stacks ranked by ImageLoad event count — answers 'which call site is loading " +
        "the most DLLs'. PerfView equivalent: 'Image Load Stacks' view. Use to distinguish eager " +
        "loads (LoadLibraryEx in main initializer) from lazy / cascading loads (CoCreateInstance, " +
        "AmsiOpenSession, EDR-injected providers). Requires stack-walk-on-ImageLoad in the capture " +
        "profile; default WPR profiles include it.")]
    public ImageLoadStacksResponse ImageLoadTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of load counts over this many buckets " +
                     "across the filter window. Default 0 = histogram off.")]
        int whenBuckets = 0,
        [Description(StackResponseOptions.CompactStacksDescription)]
        bool compactStacks = false,
        [Description(StackResponseOptions.SummaryOnlyDescription)]
        bool summaryOnly = false)
    {
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        return ImageLoadStackAnalysis.TopLoadStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid, startUs, endUs, symbolLog: Console.Error, whenBuckets: whenBuckets);
    }

    [McpServerTool, Description(
        "Caller/callee drill-down for a focus function in the image-load-stack data. Metric " +
        "is load count; top-N callers ranked by inclusive loads flowing INTO focus, callees " +
        "by loads flowing OUT to them. Use to ask 'who triggers all these calls into LdrLoadDll'.")]
    public CallerCalleeResponse ImageLoadCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus frame name, exactly as it appears in image_load_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        return ImageLoadStackAnalysis.CallerCallee(
            trace, function, top, pid, startUs, endUs, Console.Error);
    }
}
