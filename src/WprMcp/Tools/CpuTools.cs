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
        bool includeTracePct = false)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        return CpuAnalysis.TopFunctions(trace, top, pid, startUs, endUs, Console.Error, excludeEtwSelfOverhead, includeTracePct);
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
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        return Analyzers.CpuPreciseAnalysis.Analyze(trace, top, pid, startUs, endUs);
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
        bool includeTracePct = false)
    {
        if (pids is null || pids.Length == 0)
            throw new ArgumentException("pids required and must be non-empty", nameof(pids));
        Validation.RequireTop(top);

        var trace = _cache.Get(path);
        var warnings = new List<string>();
        var distinctPids = pids.Distinct().ToArray();
        IReadOnlyDictionary<int, CpuTopFunctionsResponse> result;
        try
        {
            result = CpuAnalysis.TopFunctionsMultiPid(
                trace,
                top,
                distinctPids,
                startUs,
                endUs,
                Console.Error,
                excludeEtwSelfOverhead,
                includeTracePct,
                warnings);
        }
        catch (Exception ex)
        {
            result = new Dictionary<int, CpuTopFunctionsResponse>();
            warnings.Add(ex.Message);
        }
        return new CpuTopFunctionsBatchResponse(result, warnings);
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
        bool excludeEtwSelfOverhead = false)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        return CpuAnalysis.CallerCallee(
            trace, function, top, pid, startUs, endUs, Console.Error, excludeEtwSelfOverhead);
    }
}
