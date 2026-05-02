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

    [McpServerTool, Description("Top N hot functions by exclusive CPU sample count.")]
    public CpuTopFunctionsResponse CpuTopFunctions(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null,
        [Description("Fold known ETW-overhead frames (EtwpLogKernelEvent, RtlpWalkFrameChain, etc.) into a single [ETW Overhead] bucket. Default false.")]
        bool excludeEtwSelfOverhead = false)
    {
        if (top <= 0 || top > 1000) throw new ArgumentOutOfRangeException(nameof(top));
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
        if (top <= 0 || top > 1000) throw new ArgumentOutOfRangeException(nameof(top));

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
}
