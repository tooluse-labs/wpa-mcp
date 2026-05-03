using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Substring case-insensitive match over event names (and TaskName when present)
// across all providers. Useful for locating ETW markers like [PDF_LOAD] or
// built-in events such as PageFault and CSwitch.
//
// Three modes:
//   - "count_by_event"   (default) — group matches by EventName, return counts.
//                          Avoids the 80k-char output blowup that full row mode caused
//                          when a substring matched the most common event types.
//   - "count_by_process" — group matches by ProcessName, return counts.
//   - "rows"             — full event details. Field values truncated to fieldMaxChars
//                          (default 256) so a single multi-KB payload can't flood the
//                          response.
//
// The empty-string check is duplicated here as defense-in-depth so the analyzer
// remains self-contained when called directly from tests; the tool entry-point
// also validates inputs before doing any work.
public static class MarkerSearch
{
    public const string ModeCountByEvent = "count_by_event";
    public const string ModeCountByProcess = "count_by_process";
    public const string ModeRows = "rows";

    public static MarkerSearchResponse Find(
        TraceLog trace,
        string nameSubstring,
        int top,
        string mode = ModeCountByEvent,
        int fieldMaxChars = 256)
    {
        if (string.IsNullOrEmpty(nameSubstring))
            throw new ArgumentException("nameSubstring required", nameof(nameSubstring));
        if (top <= 0) throw new ArgumentOutOfRangeException(nameof(top));
        if (fieldMaxChars < 0) throw new ArgumentOutOfRangeException(nameof(fieldMaxChars));

        var normalized = mode?.ToLowerInvariant() ?? ModeCountByEvent;
        return normalized switch
        {
            ModeCountByEvent => CountBy(trace, nameSubstring, top, byProcess: false),
            ModeCountByProcess => CountBy(trace, nameSubstring, top, byProcess: true),
            ModeRows => CollectRows(trace, nameSubstring, top, fieldMaxChars),
            _ => throw new ArgumentException(
                $"unknown mode '{mode}'. Use '{ModeCountByEvent}', '{ModeCountByProcess}', or '{ModeRows}'.",
                nameof(mode)),
        };
    }

    private static MarkerSearchResponse CountBy(
        TraceLog trace, string nameSubstring, int top, bool byProcess)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0;
        foreach (var ev in trace.Events)
        {
            if (!Matches(ev, nameSubstring)) continue;
            total++;
            var key = byProcess ? (ev.ProcessName ?? string.Empty) : ev.EventName;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        var rows = StackSourceTopN.TopByValue(counts, top, (k, v) => new MarkerCountRow(k, v));
        return new MarkerSearchResponse(
            Mode: byProcess ? ModeCountByProcess : ModeCountByEvent,
            TotalMatched: total,
            Counts: rows,
            Rows: null);
    }

    private static MarkerSearchResponse CollectRows(
        TraceLog trace, string nameSubstring, int top, int fieldMaxChars)
    {
        var rows = new List<MarkerRow>(Math.Min(top, 256));
        long total = 0;
        foreach (var ev in trace.Events)
        {
            if (!Matches(ev, nameSubstring)) continue;
            total++;
            if (rows.Count >= top) continue; // keep counting, but stop materializing rows
            var fields = new Dictionary<string, string>(ev.PayloadNames.Length);
            for (var i = 0; i < ev.PayloadNames.Length; i++)
            {
                var v = ev.PayloadValue(i);
                var s = v?.ToString() ?? "<null>";
                if (fieldMaxChars > 0 && s.Length > fieldMaxChars)
                    s = s.Substring(0, fieldMaxChars) + "…";
                fields[ev.PayloadNames[i]] = s;
            }
            rows.Add(new MarkerRow(
                TimeUs: (long)(ev.TimeStampRelativeMSec * 1000),
                Provider: ev.ProviderName,
                EventName: ev.EventName,
                ProcessName: ev.ProcessName ?? string.Empty,
                ThreadId: ev.ThreadID,
                Fields: fields));
        }
        return new MarkerSearchResponse(
            Mode: ModeRows,
            TotalMatched: total,
            Counts: null,
            Rows: rows);
    }

    private static bool Matches(TraceEvent ev, string nameSubstring) =>
        ev.EventName.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase) ||
        (ev.TaskName?.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase) ?? false);
}
