using System.ComponentModel;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class ClrTools
{
    private readonly TraceCache _cache;
    public ClrTools(TraceCache cache) => _cache = cache;

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        ".NET CLR GC analysis — list of garbage collections in the trace, with wall " +
        "duration AND 'stop the world' pause time per GC.  PerfView equivalent: 'GCStats'.  " +
        "Each row carries Generation (0/1/2), Reason (Induced / AllocSmall / AllocLarge / etc.), " +
        "DurationUs (GCStart→GCStop wall interval), and PauseUs (covering GCSuspendEEStart→ " +
        "GCRestartEEStop interval — the time mutator threads were halted).  Aggregate fields " +
        "TotalGcUs and TotalPauseUs make it easy to see 'is this app GC-bound'.  Requires " +
        "Microsoft-Windows-DotNETRuntime ETW provider with the GC keyword in the capture profile.")]
    public GcAnalysisResponse ClrGcAnalysis(
        [Description("Absolute path to .etl file")] string path,
        [Description("Filter to a single process ID (recommended — without it, all PIDs share rows)")]
        int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Optional exact process start in trace-relative microseconds; requires pid. Without it, pid-only queries explicitly aggregate intersecting lifetimes while rows remain separated by ProcessStartUs.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(
            pid, tid: null, processStartUs, threadStartUs: null);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        return Analyzers.GcAnalysis.Analyze(
            trace, pid, window.StartUs, window.EndUs, processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        ".NET CLR JIT compilation analysis — top-N methods ranked by JIT duration.  PerfView " +
        "equivalent: 'JIT Stats'.  Matches MethodJittingStarted→MethodLoadVerbose by " +
        "(ProcessID, MethodID) to compute per-method JIT μs.  Each row gives full method name " +
        "(namespace.method + signature), JitDurationUs, and the resulting native code size.  " +
        "R2R / NGen / pre-jitted methods don't fire MethodJittingStarted, so they're invisible " +
        "here — which is correct for 'what's the JIT cost in this trace'.  Requires " +
        "Microsoft-Windows-DotNETRuntime ETW provider with the JIT keyword.")]
    public JitAnalysisResponse ClrJitAnalysis(
        [Description("Absolute path to .etl file")] string path,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Top N methods by JIT duration (default 50, max 1000)")] int top = 50,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Optional exact process start in trace-relative microseconds; requires pid. Without it, pid-only queries explicitly aggregate intersecting lifetimes while rows remain separated by ProcessStartUs.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(
            pid, tid: null, processStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        return Analyzers.JitAnalysis.Analyze(
            trace, pid, top, window.StartUs, window.EndUs, processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Top-N call stacks ranked by managed-heap allocation bytes.  PerfView equivalent: " +
        "'GC Heap Alloc Stacks'.  Driven by GCAllocationTick events (CLR fires one every " +
        "~100 KB allocated per (heap, generation, type)) — sampled, not exhaustive, but " +
        "statistically meaningful for hot allocators.  Each stack carries " +
        "Exclusive/InclusiveBytes and event counts.  Response also includes TopTypes (top " +
        "allocated type names by total bytes).  Requires Microsoft-Windows-DotNETRuntime " +
        "with the GC keyword.")]
    public ClrAllocStacksResponse ClrAllocTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N stacks by exclusive allocation bytes (default 50, max 1000)")] int top = 50,
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
        return ClrAllocStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid,
            window.StartUs, window.EndUs, Console.Error, whenBuckets,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue,
            processStartUs: processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Caller-callee drill-down on a focus frame in the managed-allocation stack source.  " +
        "Metric is allocation bytes; top-N callers ranked by inclusive bytes flowing INTO " +
        "focus, callees by bytes OUT.")]
    public CallerCalleeResponse ClrAllocCallerCallee(
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
        return ClrAllocStackAnalysis.CallerCallee(
            trace, focusFunction, top, pid, window.StartUs, window.EndUs, Console.Error,
            processStartUs,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Top-N call stacks ranked by .NET exception throw count.  PerfView equivalent: " +
        "'Exceptions Stacks'.  Fires once per *thrown* exception (rethrows are separate " +
        "events).  Useful for 'is this code path throwing 1000 exceptions per second' / " +
        "'where is FormatException being swallowed in a retry loop'.  Response also " +
        "includes TopTypes (top exception type names by count).  Requires " +
        "Microsoft-Windows-DotNETRuntime with the Exception keyword.")]
    public ClrExceptionStacksResponse ClrExceptionTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N stacks by exclusive exception count (default 50, max 1000)")] int top = 50,
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
        return ClrExceptionStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid,
            window.StartUs, window.EndUs, Console.Error, whenBuckets,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue,
            processStartUs: processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Caller-callee drill-down on a focus frame in the .NET exception stack source.  " +
        "Metric is exception count; top-N callers ranked by inclusive count flowing INTO " +
        "focus, callees by count OUT.")]
    public CallerCalleeResponse ClrExceptionCallerCallee(
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
        return ClrExceptionStackAnalysis.CallerCallee(
            trace, focusFunction, top, pid, window.StartUs, window.EndUs, Console.Error,
            processStartUs,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Top-N call stacks ranked by .NET monitor-contention μs (managed `lock` / " +
        "Monitor.Enter waits).  PerfView equivalent: 'Monitor Contention Stacks'.  Matches " +
        "ContentionStart→ContentionStop by ThreadID; metric is the wait duration in " +
        "microseconds.  Filters to ContentionFlags.Managed only — native lock contention " +
        "from the same provider is excluded.  Requires Microsoft-Windows-DotNETRuntime with " +
        "the Contention keyword.")]
    public ClrContentionStacksResponse ClrContentionTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N stacks by exclusive blocked μs (default 50, max 1000)")] int top = 50,
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
        return ClrContentionStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid,
            window.StartUs, window.EndUs, Console.Error, whenBuckets,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue,
            processStartUs: processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Caller-callee drill-down on a focus frame in the .NET monitor-contention stack " +
        "source.  Metric is blocked μs; top-N callers ranked by inclusive μs flowing INTO " +
        "focus, callees by μs OUT.")]
    public CallerCalleeResponse ClrContentionCallerCallee(
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
        return ClrContentionStackAnalysis.CallerCallee(
            trace, focusFunction, top, pid, window.StartUs, window.EndUs, Console.Error,
            processStartUs,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        ".NET CLR managed-heap snapshot timeline — one row per GCHeapStats event (the CLR " +
        "fires this once at the end of each GC), with TotalHeapBytes, Gen0/1/2/LOH/POH " +
        "sizes, PinnedObjectCount, and GcHandleCount.  PerfView surfaces this in 'GCStats' " +
        "as the per-GC snapshot table; here it is a chronological time series for checking " +
        "whether observed heap or pinned-object snapshots trend upward. A trend is not by " +
        "itself proof of a leak or an object-retention path. This avoids " +
        "orchestrating multiple calls.  Pairs naturally with clr_gc_analysis (same trace, " +
        "different aggregation). Because this is a lifecycle trend, a reused PID is rejected " +
        "unless processStartUs selects exactly one process lifetime. Requires Microsoft-Windows-DotNETRuntime with the GC " +
        "keyword. A missing exact instance returns ScopeStatus=scope_not_found rather than falling back.")]
    public GcHeapStatsResponse ClrGcHeapStats(
        [Description("Absolute path to .etl file")] string path,
        [Description("Filter to a single process ID (recommended)")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Exact process start in trace-relative microseconds. Required when pid has multiple lifetimes.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(
            pid, tid: null, processStartUs, threadStartUs: null);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        return GcHeapStatsAnalysis.Analyze(
            trace, pid, window.StartUs, window.EndUs, processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        ".NET CLR finalizer analysis — top types finalized + observed finalizer-thread execution batches. " +
        "Two related streams matched here: GCFinalizeObject (per-object, carries TypeName) " +
        "is aggregated to the TopTypes table; GCFinalizersStart/Stop bracket each run of " +
        "the finalizer thread, with the Stop event carrying the count of finalizers run in " +
        "that batch — those become the Batches list. Use this as evidence when investigating " +
        "whether finalizer work overlaps GC delays, and to identify types observed being " +
        "finalized. The stream does not by itself attribute a slow GC or identify the allocation " +
        "site; clr_alloc_top_stacks can supply separate allocator evidence. Requires " +
        "Microsoft-Windows-DotNETRuntime with the GC keyword.")]
    public FinalizerAnalysisResponse ClrFinalizerAnalysis(
        [Description("Absolute path to .etl file")] string path,
        [Description("Filter to a single process ID (recommended)")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Optional exact process start in trace-relative microseconds; requires pid. Without it, pid-only queries explicitly aggregate intersecting lifetimes while batches remain separated by ProcessStartUs and TopTypes covers the aggregate scope.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(
            pid, tid: null, processStartUs, threadStartUs: null);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        return FinalizerAnalysis.Analyze(
            trace, pid, window.StartUs, window.EndUs, processStartUs);
    }
}
