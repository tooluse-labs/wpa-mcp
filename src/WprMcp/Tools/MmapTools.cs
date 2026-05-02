using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class MmapTools
{
    private readonly TraceCache _cache;
    public MmapTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Top N memory-mapped files by hard page-in bytes. " +
        "Identifies which mmap'd file caused page-in load (e.g., a network-share PDF).")]
    public MmapHotFilesResponse MmapHotFiles(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null)
    {
        if (top <= 0 || top > 1000) throw new ArgumentOutOfRangeException(nameof(top));
        var trace = _cache.Get(path);
        return MmapAnalysis.HotFiles(trace, top, pid);
    }
}
