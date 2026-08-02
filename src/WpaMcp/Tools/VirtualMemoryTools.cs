using System.ComponentModel;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class VirtualMemoryTools
{
    private readonly TraceCache _cache;
    private readonly IPrivacyLogSink _privacyLog;
    public VirtualMemoryTools(TraceCache cache, IPrivacyLogSink? privacyLog = null)
    {
        _cache = cache;
        _privacyLog = privacyLog ?? PassThroughPrivacyLogSink.Instance;
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Process memory resource snapshots from Memory/ProcessMemInfo plus observed handle " +
        "create/close deltas. Reports working set, commit, derived private bytes, private " +
        "working set, virtual size, handle deltas, observed pool allocation/free deltas, and " +
        "a memory-pressure summary with minimum free bytes, minimum free+zero bytes when " +
        "zero-page data is present, plus peak observed sampled-process batch totals across " +
        "the selected window. These pressure totals are ETW sample-batch evidence, not " +
        "complete whole-system memory accounting. " +
        "SystemMemory and system-pressure fields remain window-global when pid/processStartUs " +
        "is supplied; sampled-process totals and ranked rows follow the selected process scope. " +
        "MatchedEventCount counts in-scope ProcessMemInfo entries plus handle and pool events; " +
        "Pressure.SystemSampleCount is separate. Empty results distinguish scope_not_found, " +
        "event_class_not_observed, and no_events_in_scope; absence does not prove a keyword was disabled. " +
        "Pool rows are not absolute current counters: Entry endpoints are paired over the full " +
        "trace before window projection, and a paired free is attributed to the allocation owner " +
        "rather than the thread/process executing the free. UnknownFreeCount is reserved for a " +
        "free with no resolvable allocation anywhere in the trace. Requires MemoryInfoWS " +
        "for process snapshots and Handle/Pool for handle or pool events; use MemoryCapture.wprp " +
        "when resident footprint, handle-leak, or pool-growth questions matter. " +
        "Process rows are ordered by current working set bytes; handle rows by absolute net " +
        "delta. Neither order implies severity or causality.")]
    public MemoryResourceResponse MemoryResourceAnalysis(
        [Description("Canonical TraceId returned by load_trace")] string path,
        [Description("Top N process and handle rows (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Optional process lifetime start in microseconds; requires pid. Without it, a reused PID is explicitly returned as pid_aggregate with per-instance rows.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid: null, processStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        return Analyzers.MemoryResourceAnalysis.Analyze(
            trace, top, pid, window.StartUs, window.EndUs, processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Top-N call stacks ranked by observed VirtualAlloc operation bytes. PerfView equivalent: " +
        "'VirtualAlloc Stacks' view. VirtualMemAlloc and VirtualMemFree Length values are both " +
        "positive call-tree weights; exact allocation and free totals are reported separately. " +
        "TotalOperationBytes is traffic, and NetObservedOperationBytes is alloc minus free event " +
        "traffic—not live virtual size, commit, retained memory, or proof of a leak. Per-frame " +
        "byte metrics use the response's precision contract. Requires the VirtualAlloc keyword in the capture " +
        "profile (default WPR 'CPU' / 'CPU.light' profiles do NOT enable it).")]
    public VirtualAllocStacksResponse VirtualAllocTopStacks(
        [Description("Canonical TraceId returned by load_trace")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of alloc+free operation bytes over this many " +
                     "buckets across the filter window. Default 0 = histogram off.")]
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
        return VirtualAllocStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid,
            window.StartUs, window.EndUs, symbolLog: _privacyLog.Writer, whenBuckets: whenBuckets,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue,
            processStartUs: processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Caller/callee drill-down for a focus function in VirtualAlloc-stack data. Metric is " +
        "virtualMemoryOperationBytes: alloc and free Length values are both positive weights. " +
        "This is operation traffic, not retained virtual memory or leak evidence. Top-N callers " +
        "are ranked by bytes flowing INTO focus and callees by bytes OUT.")]
    public CallerCalleeResponse VirtualAllocCallerCallee(
        [Description("Canonical TraceId returned by load_trace")] string path,
        [Description("Focus frame name, exactly as it appears in virtual_alloc_top_stacks output.")]
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
        return VirtualAllocStackAnalysis.CallerCallee(
            trace, function, top, pid, window.StartUs, window.EndUs, _privacyLog.Writer,
            processStartUs,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue);
    }
}
