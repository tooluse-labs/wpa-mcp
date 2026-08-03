using System.ComponentModel;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;
using static WpaMcp.Analyzers.MarkerSearch;

namespace WpaMcp.Tools;

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
        "to choose windows for downstream analyzers. An empty result returns no_name_match; it " +
        "does not establish that a provider or capture keyword was disabled.")]
    public MarkerSearchResponse FindMarker(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Substring to match against event/task names")] string nameSubstring,
        [Description("Top N rows (counts: top buckets; rows: max events) (default 50, max 1000)")] int top = 50,
        [Description("'count_by_event' (default), 'count_by_process', or 'rows'")] string mode = ModeCountByEvent,
        [Description("In rows mode, max chars per Fields value (default 256)")] int fieldMaxChars = 256)
    {
        Validation.RequireTop(top);
        Validation.RequireText(nameSubstring);
        Validation.RequireText(mode);
        if (fieldMaxChars < 0 || fieldMaxChars > Validation.MaxStringChars)
            throw new ArgumentOutOfRangeException(nameof(fieldMaxChars));
        using var traceLease = _cache.Acquire(traceId);
        var trace = traceLease.Trace;
        return MarkerSearch.Find(trace, nameSubstring, top, mode, fieldMaxChars);
    }
}
