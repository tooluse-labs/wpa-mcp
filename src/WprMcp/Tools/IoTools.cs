using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class IoTools
{
    private readonly TraceCache _cache;
    public IoTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description("Top N files by total IO bytes (read + write).")]
    public FileIoResponse FileIoTopFiles(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        return FileIoAnalysis.TopFiles(trace, top, pid);
    }
}
