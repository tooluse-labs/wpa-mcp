using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class MarkerTools
{
    private readonly TraceCache _cache;
    public MarkerTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description("Searches all events for those whose name or task contains the given substring (case-insensitive).")]
    public MarkerSearchResponse FindMarker(
        [Description("Absolute path to .etl file")] string path,
        [Description("Substring to match against event/task names")] string nameSubstring,
        [Description("Max rows (default 100, max 1000)")] int top = 100)
    {
        if (top <= 0 || top > 1000) throw new ArgumentOutOfRangeException(nameof(top));
        // Empty-substring check fires BEFORE _cache.Get so callers get an
        // ArgumentException for invalid input rather than FileNotFoundException
        // when the path also happens to be missing. The analyzer re-validates
        // for defense-in-depth when called directly.
        if (string.IsNullOrEmpty(nameSubstring))
            throw new ArgumentException("nameSubstring required", nameof(nameSubstring));
        var trace = _cache.Get(path);
        return MarkerSearch.Find(trace, nameSubstring, top);
    }
}
