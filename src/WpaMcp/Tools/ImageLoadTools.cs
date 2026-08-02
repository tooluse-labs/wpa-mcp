using System.ComponentModel;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class ImageLoadTools
{
    private readonly TraceCache _cache;
    private readonly IPrivacyLogSink _privacyLog;
    private readonly QueryResultCursorCoordinator _queryResults;
    public ImageLoadTools(TraceCache cache, IPrivacyLogSink? privacyLog = null)
    {
        _cache = cache;
        _privacyLog = privacyLog ?? PassThroughPrivacyLogSink.Instance;
        _queryResults = new($"direct_{Guid.NewGuid():N}", "off");
    }

    public ImageLoadTools(
        TraceCache cache,
        CapabilityDiscoveryRuntime capabilityDiscovery,
        IPrivacyLogSink? privacyLog = null)
    {
        _cache = cache;
        _privacyLog = privacyLog ?? PassThroughPrivacyLogSink.Instance;
        _queryResults = capabilityDiscovery.QueryResults;
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Per-process DLL/image-load timeline in chronological order — every ImageLoad event " +
        "with absolute timestamp, offset from ProcessStart, and gap from the previous load.  " +
        "PerfView equivalent: filter the 'Events' view to ImageLoad for one PID (no native " +
        "composite view). Use to spot late ImageLoad events and unusually long inter-event gaps. " +
        "A gap does not identify the intervening work or measure one DLL's map duration. " +
        "Pair with image_load_top_gaps (same data ranked by gap, with FirstLoadOffsetUs) and " +
        "image_load_top_stacks (the call stack attached to each load event, when captured). For load *durations* " +
        "(not gaps between loads), combine with wait_analysis on the PID's main thread.  " +
        "Requires the Loader keyword (default WPR profiles include it). No startUs/endUs: this is " +
        "a single process-instance image-load lifecycle timeline; when a PID was reused, pass " +
        "processStartUs from list_processes. Clean reuse returns structured " +
        "ScopeStatus/NoDataReason=process_start_required with candidate keys; conflicting lifetime evidence " +
        "returns ambiguous_process_instance, and a missing exact instance returns scope_not_found. " +
        "Rows use exact cursor pagination ordered by TimeUs then EventIndex; TotalImageLoads " +
        "remains the exact full-result total. Follow nextCursor until hasMore=false. " +
        "Startup-relative offsets are null unless an observed ProcessStart establishes the baseline; " +
        "ProcessStartEvidenceState reports that boundary explicitly. " +
        "Use image_load_top_stacks for windowed stacks.")]
    public ImageLoadTimingResponse ImageLoadTiming(
        [Description("Canonical TraceId returned by load_trace")] string path,
        [Description("Process ID")] int pid,
        [Description("Maximum image loads to return in this page (default 100, max 1000). This does not change TotalImageLoads.")] int pageSize = 100,
        [Description("Exact process start in trace-relative microseconds. Required when the PID has multiple lifetimes.")]
        long? processStartUs = null,
        [Description("Opaque qrc_ continuation returned by the previous page. It is bound to the principal/session, trace generation, tool contract, symbol/privacy context, normalized query, scope, and ordering.")]
        string? cursor = null)
    {
        Validation.RequireThreadSelector(pid, tid: null, processStartUs, threadStartUs: null);
        Validation.RequireTop(pageSize);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var query = TimelinePagination.CanonicalQuery(
            TimelinePagination.ImageLoadTimingTool,
            ("pid", TimelinePagination.Number(pid)),
            ("processStartUs", TimelinePagination.OptionalNumber(processStartUs)),
            ("pageSize", TimelinePagination.Number(pageSize)));
        var context = TimelinePagination.CreateContext(
            traceLease,
            path,
            TimelinePagination.ImageLoadTimingTool,
            query,
            TimelinePagination.ImageLoadTimingOrdering);
        var position = _queryResults.ResolveTimeline(context, cursor);
        return ImageLoadAnalysis.PerProcessPage(
            trace, pid, pageSize, processStartUs, position, context);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Top-N image loads with the LARGEST gap from the previous load (chronological). Use to " +
        "spot long intervals between adjacent ImageLoad events. Response also carries " +
        "FirstLoadOffsetUs, the ProcessStart-to-first-ImageLoad interval. Neither interval " +
        "identifies callbacks, scanning, suspension, scheduling, or another mechanism. Pairs with image_load_timing " +
        "(chronological list) — same data, different ordering. No startUs/endUs: gaps are computed " +
        "over one process-instance lifecycle; reused PIDs require processStartUs. Clean reuse returns " +
        "structured process_start_required with candidate keys; conflicting lifetime evidence returns " +
        "ambiguous_process_instance, and a missing exact instance returns scope_not_found.")]
    public ImageLoadTopGapsResponse ImageLoadTopGaps(
        [Description("Canonical TraceId returned by load_trace")] string path,
        [Description("Process ID")] int pid,
        [Description("Top N gap rows (default 20, max 1000)")] int top = 20,
        [Description("Exact process start in trace-relative microseconds. Required when the PID has multiple lifetimes.")]
        long? processStartUs = null)
    {
        Validation.RequireThreadSelector(pid, tid: null, processStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        return ImageLoadAnalysis.TopGaps(trace, pid, top, processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Top-N call stacks ranked by ImageLoad event count — answers 'which call site is loading " +
        "the most DLLs'. PerfView equivalent: 'Image Load Stacks' view. Use to distinguish eager " +
        "loads (LoadLibraryEx in main initializer) from lazy / cascading loads (CoCreateInstance, " +
        "AmsiOpenSession, EDR-injected providers). Requires stack-walk-on-ImageLoad in the capture " +
        "profile; default WPR profiles include it. StackCoverage is ImageLoad-only; ?!? is " +
        "synthetic unknown evidence, not a captured loader call chain.")]
    public ImageLoadStacksResponse ImageLoadTopStacks(
        [Description("Canonical TraceId returned by load_trace")] string path,
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
        return ImageLoadStackAnalysis.TopLoadStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid,
            window.StartUs, window.EndUs, symbolLog: _privacyLog.Writer, whenBuckets: whenBuckets,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue,
            processStartUs: processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Caller/callee drill-down for a focus function in the image-load-stack data. Metric " +
        "is load count; top-N callers ranked by inclusive loads flowing INTO focus, callees " +
        "by loads flowing OUT to them. This is associated stack evidence for calls into loader " +
        "frames; it does not prove the higher-level cause of each load.")]
    public CallerCalleeResponse ImageLoadCallerCallee(
        [Description("Canonical TraceId returned by load_trace")] string path,
        [Description("Focus frame name, exactly as it appears in image_load_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
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
        Validation.RequireFunctionName(function);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return ImageLoadStackAnalysis.CallerCallee(
            trace, function, top, pid, window.StartUs, window.EndUs, _privacyLog.Writer,
            processStartUs,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue);
    }
}
