using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class WaitTools
{
    private readonly TraceCache _cache;
    public WaitTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Per-thread blocked-time analysis from CSwitch events. Surfaces threads spending wall-clock " +
        "time blocked rather than running on CPU — the canonical answer to 'why was this slow?' when " +
        "CPU usage is low. Each row carries the dominant wait reasons (e.g., WrFilterContext = blocked " +
        "in a Filter Manager minifilter callback). Requires the CSwitch keyword in the capture profile.")]
    public WaitAnalysisResponse WaitAnalysis(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null)
    {
        if (top <= 0 || top > 1000) throw new ArgumentOutOfRangeException(nameof(top));
        var trace = _cache.Get(path);
        return Analyzers.WaitAnalysis.Analyze(trace, top, pid, startUs, endUs);
    }
}
