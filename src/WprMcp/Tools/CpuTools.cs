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
        [Description("Window end in microseconds since trace start")] long? endUs = null)
    {
        if (top <= 0 || top > 1000) throw new ArgumentOutOfRangeException(nameof(top));
        var trace = _cache.Get(path);
        return CpuAnalysis.TopFunctions(trace, top, pid, startUs, endUs, Console.Error);
    }
}
