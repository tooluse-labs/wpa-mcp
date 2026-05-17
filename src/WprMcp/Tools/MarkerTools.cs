using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;
using static WprMcp.Analyzers.MarkerSearch;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class MarkerTools
{
    private readonly TraceCache _cache;
    public MarkerTools(TraceCache cache) => _cache = cache;

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Searches all events whose name or task contains the given substring (case-insensitive). " +
        "Default mode 'count_by_event' returns a histogram, which avoids dumping every matching row " +
        "for broad queries like 'Process'. Switch to 'rows' for full event detail. " +
        "No startUs/endUs: this is a whole-trace event-discovery scan; use returned timestamps " +
        "to choose windows for downstream analyzers.")]
    public MarkerSearchResponse FindMarker(
        [Description("Absolute path to .etl file")] string path,
        [Description("Substring to match against event/task names")] string nameSubstring,
        [Description("Top N rows (counts: top buckets; rows: max events) (default 50, max 1000)")] int top = 50,
        [Description("'count_by_event' (default), 'count_by_process', or 'rows'")] string mode = ModeCountByEvent,
        [Description("In rows mode, max chars per Fields value (default 256)")] int fieldMaxChars = 256)
    {
        Validation.RequireTop(top);
        // Empty-substring check fires BEFORE _cache.Get so callers get an
        // ArgumentException for invalid input rather than FileNotFoundException
        // when the path also happens to be missing. The analyzer re-validates
        // for defense-in-depth when called directly.
        if (string.IsNullOrEmpty(nameSubstring))
            throw new ArgumentException("nameSubstring required", nameof(nameSubstring));
        var trace = _cache.Get(path);
        return MarkerSearch.Find(trace, nameSubstring, top, mode, fieldMaxChars);
    }
}
