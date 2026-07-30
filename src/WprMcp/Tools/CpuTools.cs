using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class CpuTools
{
    private readonly TraceCache _cache;
    public CpuTools(TraceCache cache) => _cache = cache;

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Top-N hot functions by exclusive CPU sample count — the canonical 'where is CPU " +
        "time going' answer.  PerfView equivalent: 'CPU Stacks → ByName'.  Built from " +
        "per-CPU PerfInfoSample events (kernel sampler, default ~1 ms cadence per CPU); " +
        "each row's ExclusiveSamples is the count of samples whose leaf frame was THIS " +
        "function (i.e., on-CPU AT this frame, not transiting through it).  Pair with " +
        "cpu_caller_callee to drill into a specific frame, or cpu_top_functions_batch " +
        "when investigating multiple PIDs in one trace load.  On large traces, scope " +
        "via `pid` first (use `list_processes orderBy=cpu` to pick heavy hitters) — " +
        "whole-trace aggregation walks every CpuSample event and fetches PDBs for every " +
        "warm module, and can hit the MCP tool-call timeout on multi-GB traces.  By default, " +
        "filtered calls omit percent-of-entire-trace columns to avoid an extra whole-trace " +
        "sample-count pass; set includeTracePct=true only when those columns matter.  " +
        "Set excludeEtwSelfOverhead=true " +
        "to fold kernel-side stack-walk frames (EtwpLogKernelEvent etc.) into one bucket — " +
        "useful when ETW overhead drowns the workload signal.  Requires the CPU sample " +
        "keyword (default WPR 'CPU' / 'CPU.light' profiles include it).")]
    public CpuTopFunctionsResponse CpuTopFunctions(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Fold known ETW-overhead frames (EtwpLogKernelEvent, RtlpWalkFrameChain, etc.) into a single [ETW Overhead] bucket. Default false.")]
        bool excludeEtwSelfOverhead = false,
        [Description("When filtered by pid/startUs/endUs, also compute ExclusivePctOfTrace/InclusivePctOfTrace over the whole trace. Default false because it requires an extra whole-trace CPU sample count pass on large ETL files.")]
        bool includeTracePct = false,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false,
        [Description("Optional thread ID; requires pid and is resolved within the requested half-open window.")]
        int? tid = null,
        [Description("Optional exact process start in trace-relative microseconds; requires pid. Without it, pid-only queries retain aggregate behavior across process lifetimes.")]
        long? processStartUs = null,
        [Description("Optional exact thread start in trace-relative microseconds; requires pid and tid.")]
        long? threadStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid, processStartUs, threadStartUs);
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        var scope = ThreadAnalysisScope.ResolveRequired(
            window, pid, tid, processStartUs, threadStartUs, TraceIdentityIndex.For(trace));
        return CpuAnalysis.TopFunctions(
            trace, top, scope, Console.Error,
            excludeEtwSelfOverhead, includeTracePct, resolveSymbols,
            hasFilter: pid.HasValue || startUs.HasValue || endUs.HasValue ||
                       tid.HasValue || processStartUs.HasValue || threadStartUs.HasValue);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "CPU Usage (Precise)-style scheduler summary from CSwitch + ReadyThread events. " +
        "Use this when sampled CPU is insufficient: it reports actual on-CPU microseconds, " +
        "ready-to-run latency after a thread is readied, per-core runtime attribution, and " +
        "quantum/preemption signals. Requires CSwitch for CPU/core data; ReadyThread is needed " +
        "for ready latency.")]
    public CpuPreciseResponse CpuPreciseAnalysis(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N thread rows by on-CPU microseconds (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Optional thread ID; requires pid and is resolved within the requested half-open window.")]
        int? tid = null,
        [Description("Optional exact process start in trace-relative microseconds; requires pid. Without it, pid-only queries retain aggregate behavior across process lifetimes.")]
        long? processStartUs = null,
        [Description("Optional exact thread start in trace-relative microseconds; requires pid and tid.")]
        long? threadStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid, processStartUs, threadStartUs);
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        var scope = ThreadAnalysisScope.ResolveRequired(
            window, pid, tid, processStartUs, threadStartUs, TraceIdentityIndex.For(trace));
        return Analyzers.CpuPreciseAnalysis.Analyze(trace, top, scope);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Batch variant: top N hot functions per PID, in a single trace load. " +
        "Each PID gets an independent CallTree (so its inclusive-% column normalizes to that PID's samples). " +
        "Use when investigating multiple processes from the same trace — saves N round-trips.")]
    public CpuTopFunctionsBatchResponse CpuTopFunctionsBatch(
        [Description("Absolute path to .etl file")] string path,
        [Description("Process IDs to analyze (must be non-empty)")] int[] pids,
        [Description("Top N rows per PID (default 30, max 1000)")] int top = 30,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Fold known ETW-overhead frames into [ETW Overhead] bucket. Default false.")]
        bool excludeEtwSelfOverhead = false,
        [Description("When filtered by pid/startUs/endUs, also compute ExclusivePctOfTrace/InclusivePctOfTrace over the whole trace. Default false because it requires an extra whole-trace CPU sample count pass on large ETL files.")]
        bool includeTracePct = false,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false,
        [Description("Soft budget in milliseconds for batch work after trace loading. Exhaustion returns completed PID results plus skipped PID metadata before the MCP client timeout.")]
        int timeBudgetMs = 100_000)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        if (pids is null || pids.Length == 0)
            throw new ArgumentException("pids required and must be non-empty", nameof(pids));
        Validation.RequireCollectionCount(pids.Length);
        foreach (var pid in pids)
            Validation.RequirePidTid(pid, tid: null);
        Validation.RequireTop(top);
        Validation.RequireTimeBudgetMs(timeBudgetMs);

        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        var warnings = new List<string>();
        var distinctPids = pids.Distinct().ToArray();
        var skippedPids = new List<int>();
        IReadOnlyDictionary<int, CpuTopFunctionsResponse> result;
        try
        {
            result = CpuAnalysis.TopFunctionsMultiPid(
                trace,
                top,
                distinctPids,
                window.StartUs,
                window.EndUs,
                Console.Error,
                excludeEtwSelfOverhead,
                includeTracePct,
                warnings,
                resolveSymbols,
                timeBudgetMs,
                skippedPids);
        }
        catch (Exception ex)
        {
            result = new Dictionary<int, CpuTopFunctionsResponse>();
            warnings.Add(ex.Message);
        }
        return new CpuTopFunctionsBatchResponse(
            result,
            warnings,
            Partial: skippedPids.Count > 0,
            SkippedPids: skippedPids,
            RequestedPidCount: distinctPids.Length,
            CompletedPidCount: result.Count);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Caller/callee drill-down for a focus function — given a frame name (copy verbatim from " +
        "cpu_top_functions output), returns the immediate callers (frames calling INTO focus) and " +
        "callees (frames focus calls OUT to), each ranked by inclusive samples. PerfView equivalent: " +
        "the 'Callers' / 'Callees' tabs of CPU Stacks. Recursion-safe: counts the leaf-most match " +
        "of focus per stack only.")]
    public CallerCalleeResponse CpuCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus frame name, exactly as it appears in cpu_top_functions output " +
                     "(case-sensitive; unresolved frames look like 'module!?').")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Fold known ETW-overhead frames into [ETW Overhead] bucket. Default false.")]
        bool excludeEtwSelfOverhead = false,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false,
        [Description("Optional thread ID; requires pid and is resolved within the requested half-open window.")]
        int? tid = null,
        [Description("Optional exact process start in trace-relative microseconds; requires pid. Without it, pid-only queries retain aggregate behavior across process lifetimes.")]
        long? processStartUs = null,
        [Description("Optional exact thread start in trace-relative microseconds; requires pid and tid.")]
        long? threadStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid, processStartUs, threadStartUs);
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        var scope = ThreadAnalysisScope.ResolveRequired(
            window, pid, tid, processStartUs, threadStartUs, TraceIdentityIndex.For(trace));
        return CpuAnalysis.CallerCallee(
            trace, function, top, scope,
            Console.Error, excludeEtwSelfOverhead, resolveSymbols);
    }
}
