using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

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
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        var trace = _cache.Get(path);
        return Analyzers.GcAnalysis.Analyze(trace, pid, startUs, endUs);
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
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        return Analyzers.JitAnalysis.Analyze(trace, pid, top, startUs, endUs);
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
        bool summaryOnly = false)
    {
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        return ClrAllocStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid, startUs, endUs, Console.Error, whenBuckets);
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
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(focusFunction);
        var trace = _cache.Get(path);
        return ClrAllocStackAnalysis.CallerCallee(trace, focusFunction, top, pid, startUs, endUs, Console.Error);
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
        bool summaryOnly = false)
    {
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        return ClrExceptionStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid, startUs, endUs, Console.Error, whenBuckets);
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
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(focusFunction);
        var trace = _cache.Get(path);
        return ClrExceptionStackAnalysis.CallerCallee(trace, focusFunction, top, pid, startUs, endUs, Console.Error);
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
        bool summaryOnly = false)
    {
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        return ClrContentionStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid, startUs, endUs, Console.Error, whenBuckets);
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
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(focusFunction);
        var trace = _cache.Get(path);
        return ClrContentionStackAnalysis.CallerCallee(trace, focusFunction, top, pid, startUs, endUs, Console.Error);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        ".NET CLR managed-heap snapshot timeline — one row per GCHeapStats event (the CLR " +
        "fires this once at the end of each GC), with TotalHeapBytes, Gen0/1/2/LOH/POH " +
        "sizes, PinnedObjectCount, and GcHandleCount.  PerfView surfaces this in 'GCStats' " +
        "as the per-GC snapshot table; here it's a chronological time series so you can " +
        "answer 'is the heap leaking over time' / 'are pinned objects climbing' without " +
        "orchestrating multiple calls.  Pairs naturally with clr_gc_analysis (same trace, " +
        "different aggregation).  Requires Microsoft-Windows-DotNETRuntime with the GC " +
        "keyword.")]
    public GcHeapStatsResponse ClrGcHeapStats(
        [Description("Absolute path to .etl file")] string path,
        [Description("Filter to a single process ID (recommended)")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        var trace = _cache.Get(path);
        return GcHeapStatsAnalysis.Analyze(trace, pid, startUs, endUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        ".NET CLR finalizer analysis — top types finalized + finalizer-thread pause batches.  " +
        "Two related streams matched here: GCFinalizeObject (per-object, carries TypeName) " +
        "is aggregated to the TopTypes table; GCFinalizersStart/Stop bracket each run of " +
        "the finalizer thread, with the Stop event carrying the count of finalizers run in " +
        "that batch — those become the Batches list.  Use this to answer 'why are GCs slow' " +
        "(finalizers can hold up the next GC because GC waits for the finalizer queue to " +
        "drain on certain transitions) and 'what's allocating finalizable objects' (pair " +
        "TopTypes with clr_alloc_top_stacks on the offender types).  Requires " +
        "Microsoft-Windows-DotNETRuntime with the GC keyword.")]
    public FinalizerAnalysisResponse ClrFinalizerAnalysis(
        [Description("Absolute path to .etl file")] string path,
        [Description("Filter to a single process ID (recommended)")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        var trace = _cache.Get(path);
        return FinalizerAnalysis.Analyze(trace, pid, startUs, endUs);
    }
}
