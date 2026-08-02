using System.ComponentModel;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class CpuTools
{
    private readonly TraceCache _cache;
    private readonly IPrivacyLogSink _privacyLog;
    public CpuTools(TraceCache cache, IPrivacyLogSink? privacyLog = null)
    {
        _cache = cache;
        _privacyLog = privacyLog ?? PassThroughPrivacyLogSink.Instance;
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
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
        "keyword (default WPR 'CPU' / 'CPU.light' profiles include it). StackCoverage reports " +
        "the selected CPU domain only; ?!? is synthetic unknown evidence, not a captured call chain.")]
    public CpuTopFunctionsResponse CpuTopFunctions(
        [Description("Canonical TraceId returned by load_trace")] string path,
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
        long? threadStartUs = null,
        [Description("Optional exact thread generation returned by CPU/Wait thread rows; requires pid and tid. Use it when ThreadStartUs is shared by multiple generations.")]
        long? threadGeneration = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(
            pid, tid, processStartUs, threadStartUs, threadGeneration);
        Validation.RequireTop(top);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        var identities = TraceIdentityIndex.For(trace);
        var processScope = ProcessAnalysisScope.Resolve(
            window, pid, processStartUs, identities);
        var scope = ResolveStackScope(
            window, pid, tid, processStartUs, threadStartUs, identities, processScope,
            threadGeneration);
        return CpuAnalysis.TopFunctions(
            trace, top, scope, _privacyLog.Writer,
            excludeEtwSelfOverhead, includeTracePct, resolveSymbols,
            hasFilter: pid.HasValue || startUs.HasValue || endUs.HasValue ||
                       tid.HasValue || processStartUs.HasValue || threadStartUs.HasValue ||
                       threadGeneration.HasValue,
            processScope: processScope,
            traceHasCpuSamples: traceLease.Capabilities.HasCpuSamples);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "CPU Usage (Precise)-style scheduler summary from CSwitch + ReadyThread events. " +
        "Use this when sampled CPU is insufficient: it reports actual on-CPU microseconds, " +
        "ready-to-run latency after a thread is readied, per-core runtime attribution, and " +
        "quantum/preemption signals. Requires CSwitch for CPU/core data; ReadyThread is needed " +
        "for ready latency. Exact process/thread selectors that do not exist return a structured " +
        "scope_not_found response; PID-only scopes explicitly report reused-lifetime aggregation.")]
    public CpuPreciseResponse CpuPreciseAnalysis(
        [Description("Canonical TraceId returned by load_trace")] string path,
        [Description("Top N thread rows by on-CPU microseconds (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Optional thread ID; requires pid and is resolved within the requested half-open window.")]
        int? tid = null,
        [Description("Optional exact process start in trace-relative microseconds; requires pid. Without it, pid-only queries retain aggregate behavior across process lifetimes.")]
        long? processStartUs = null,
        [Description("Optional exact thread start in trace-relative microseconds; requires pid and tid.")]
        long? threadStartUs = null,
        [Description("Optional exact thread generation returned by CPU/Wait thread rows; requires pid and tid. Use it when ThreadStartUs is shared by multiple generations.")]
        long? threadGeneration = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(
            pid, tid, processStartUs, threadStartUs, threadGeneration);
        Validation.RequireTop(top);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        var identities = TraceIdentityIndex.For(trace);
        var processScope = ProcessAnalysisScope.Resolve(
            window, pid, processStartUs, identities);
        var scope = ResolveStackScope(
            window, pid, tid, processStartUs, threadStartUs, identities, processScope,
            threadGeneration);
        if (!scope.IsResolved)
            return Analyzers.CpuPreciseAnalysis.EmptyScope(scope);

        return Analyzers.CpuPreciseAnalysis.Analyze(
            trace, top, scope, processScope);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Batch variant: top N hot functions per PID, in a single trace load. " +
        "Each PID gets an independent CallTree (so its inclusive-% column normalizes to that PID's samples). " +
        "Use when investigating multiple processes from the same trace — saves N round-trips. " +
        "Optional processStartUs entries select exact process lifetimes; a null entry explicitly retains " +
        "PID aggregation. ScopeResults separates missing scopes, completed empty scopes, and budget-skipped work; " +
        "an empty CPU sample set does not prove that CPU sampling was disabled.")]
    public CpuTopFunctionsBatchResponse CpuTopFunctionsBatch(
        [Description("Canonical TraceId returned by load_trace")] string path,
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
        int timeBudgetMs = 100_000,
        [Description("Optional process start selectors aligned one-for-one with pids. A null entry requests explicit PID aggregation; a non-null entry selects that exact trace-relative process start.")]
        long?[]? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        var selectors = CpuAnalysis.NormalizeBatchSelectors(pids, processStartUs);
        Validation.RequireTop(top);
        Validation.RequireTimeBudgetMs(timeBudgetMs);

        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        var execution = CpuAnalysis.ExecuteTopFunctionsBatch(
            trace,
            top,
            selectors,
            window,
            _privacyLog.Writer,
            excludeEtwSelfOverhead,
            includeTracePct,
            resolveSymbols,
            timeBudgetMs,
            traceHasCpuSamples: traceLease.Capabilities.HasCpuSamples);
        return new CpuTopFunctionsBatchResponse(
            execution.PerPid,
            execution.Warnings,
            Partial: execution.Partial,
            SkippedPids: execution.SkippedPids,
            RequestedPidCount: selectors.Count,
            CompletedPidCount: execution.CompletedPids.Count,
            CompletedPids: execution.CompletedPids,
            PidsNotFound: execution.PidsNotFound,
            PidsWithNoSamples: execution.PidsWithNoSamples,
            ScopeResults: execution.ScopeResults);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Caller/callee drill-down for a focus function — given a frame name (copy verbatim from " +
        "cpu_top_functions output), returns the immediate callers (frames calling INTO focus) and " +
        "callees (frames focus calls OUT to), each ranked by inclusive samples. PerfView equivalent: " +
        "the 'Callers' / 'Callees' tabs of CPU Stacks. Recursion-safe: counts the leaf-most match " +
        "of focus per stack only.")]
    public CallerCalleeResponse CpuCallerCallee(
        [Description("Canonical TraceId returned by load_trace")] string path,
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
        long? threadStartUs = null,
        [Description("Optional exact thread generation returned by CPU/Wait thread rows; requires pid and tid. Use it when ThreadStartUs is shared by multiple generations.")]
        long? threadGeneration = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(
            pid, tid, processStartUs, threadStartUs, threadGeneration);
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        var identities = TraceIdentityIndex.For(trace);
        var processScope = ProcessAnalysisScope.Resolve(
            window, pid, processStartUs, identities);
        var scope = ResolveStackScope(
            window, pid, tid, processStartUs, threadStartUs, identities, processScope,
            threadGeneration);
        return CpuAnalysis.CallerCallee(
            trace, function, top, scope,
            _privacyLog.Writer, excludeEtwSelfOverhead, resolveSymbols, processScope,
            traceHasCpuSamples: traceLease.Capabilities.HasCpuSamples);
    }

    internal static ThreadAnalysisScope ResolveStackScope(
        TimeWindow window,
        int? pid,
        int? tid,
        long? processStartUs,
        long? threadStartUs,
        TraceIdentityIndex identities,
        ProcessAnalysisScope? processScope = null,
        long? threadGeneration = null)
    {
        processScope ??= ProcessAnalysisScope.Resolve(
            window, pid, processStartUs, identities);
        var resolution = ThreadAnalysisScope.Resolve(
            window, pid, tid, processStartUs, threadStartUs, identities,
            threadGeneration);
        return ThreadAnalysisScope.Materialize(
            window,
            pid,
            tid,
            processStartUs,
            threadStartUs,
            identities,
            processScope,
            resolution,
            threadGeneration);
    }
}
