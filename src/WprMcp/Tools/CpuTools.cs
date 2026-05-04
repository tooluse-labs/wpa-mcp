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

    [McpServerTool, Description(
        "Top-N hot functions by exclusive CPU sample count — the canonical 'where is CPU " +
        "time going' answer.  PerfView equivalent: 'CPU Stacks → ByName'.  Built from " +
        "per-CPU PerfInfoSample events (kernel sampler, default ~1 ms cadence per CPU); " +
        "each row's ExclusiveSamples is the count of samples whose leaf frame was THIS " +
        "function (i.e., on-CPU AT this frame, not transiting through it).  Pair with " +
        "cpu_caller_callee to drill into a specific frame, or cpu_top_functions_batch " +
        "when investigating multiple PIDs in one trace load.  Set excludeEtwSelfOverhead=true " +
        "to fold kernel-side stack-walk frames (EtwpLogKernelEvent etc.) into one bucket — " +
        "useful when ETW overhead drowns the workload signal.  Requires the CPU sample " +
        "keyword (default WPR 'CPU' / 'CPU.light' profiles include it).")]
    public CpuTopFunctionsResponse CpuTopFunctions(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null,
        [Description("Fold known ETW-overhead frames (EtwpLogKernelEvent, RtlpWalkFrameChain, etc.) into a single [ETW Overhead] bucket. Default false.")]
        bool excludeEtwSelfOverhead = false)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        return CpuAnalysis.TopFunctions(trace, top, pid, startUs, endUs, Console.Error, excludeEtwSelfOverhead);
    }

    [McpServerTool, Description(
        "Batch variant: top N hot functions per PID, in a single trace load. " +
        "Each PID gets an independent CallTree (so its inclusive-% column normalizes to that PID's samples). " +
        "Use when investigating multiple processes from the same trace — saves N round-trips.")]
    public CpuTopFunctionsBatchResponse CpuTopFunctionsBatch(
        [Description("Absolute path to .etl file")] string path,
        [Description("Process IDs to analyze (must be non-empty)")] int[] pids,
        [Description("Top N rows per PID (default 30, max 1000)")] int top = 30,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null,
        [Description("Fold known ETW-overhead frames into [ETW Overhead] bucket. Default false.")]
        bool excludeEtwSelfOverhead = false)
    {
        if (pids is null || pids.Length == 0)
            throw new ArgumentException("pids required and must be non-empty", nameof(pids));
        Validation.RequireTop(top);

        var trace = _cache.Get(path);
        var result = new Dictionary<int, CpuTopFunctionsResponse>();
        var warnings = new List<string>();
        foreach (var p in pids.Distinct())
        {
            try
            {
                result[p] = CpuAnalysis.TopFunctions(trace, top, p, startUs, endUs, Console.Error, excludeEtwSelfOverhead);
            }
            catch (Exception ex)
            {
                warnings.Add($"pid {p}: {ex.Message}");
            }
        }
        return new CpuTopFunctionsBatchResponse(result, warnings);
    }

    [McpServerTool, Description(
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
        [Description("Window end in microseconds since trace start")] long? endUs = null,
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
