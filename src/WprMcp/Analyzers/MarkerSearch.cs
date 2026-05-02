using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Substring case-insensitive match over event names (and TaskName when present)
// across all providers. Useful for locating ETW markers like [PDF_LOAD] or
// built-in events such as PageFault and CSwitch.
//
// The empty-string check is duplicated here as defense-in-depth so the analyzer
// remains self-contained when called directly from tests; the tool entry-point
// also validates inputs before doing any work.
public static class MarkerSearch
{
    public static MarkerSearchResponse Find(TraceLog trace, string nameSubstring, int top)
    {
        if (string.IsNullOrEmpty(nameSubstring))
            throw new ArgumentException("nameSubstring required", nameof(nameSubstring));

        var rows = new List<MarkerRow>();
        foreach (var ev in trace.Events)
        {
            if (rows.Count >= top) break;
            if (ev.EventName.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase) ||
                (ev.TaskName?.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                var fields = new Dictionary<string, string>(ev.PayloadNames.Length);
                for (var i = 0; i < ev.PayloadNames.Length; i++)
                {
                    var v = ev.PayloadValue(i);
                    fields[ev.PayloadNames[i]] = v?.ToString() ?? "<null>";
                }
                rows.Add(new MarkerRow(
                    TimeUs: (long)(ev.TimeStampRelativeMSec * 1000),
                    Provider: ev.ProviderName,
                    EventName: ev.EventName,
                    ProcessName: ev.ProcessName ?? string.Empty,
                    ThreadId: ev.ThreadID,
                    Fields: fields));
            }
        }
        return new MarkerSearchResponse(rows);
    }
}
