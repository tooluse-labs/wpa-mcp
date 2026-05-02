using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class ImageLoadTools
{
    private readonly TraceCache _cache;
    public ImageLoadTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Per-process DLL/image-load sequence in chronological order, with offset-from-process-start. " +
        "Use to spot late-loading DLLs or unusually long gaps that hint at minifilter / sig-scan delays " +
        "between loads. For load *durations*, combine with wait_analysis on the same PID's main thread.")]
    public ImageLoadTimingResponse ImageLoadTiming(
        [Description("Absolute path to .etl file")] string path,
        [Description("Process ID")] int pid,
        [Description("Top N loads (default 100, max 1000)")] int top = 100)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        return ImageLoadAnalysis.PerProcess(trace, pid, top);
    }
}
