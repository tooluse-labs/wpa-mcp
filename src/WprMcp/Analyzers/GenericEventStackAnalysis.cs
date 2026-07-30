using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Top stacks ranked by event count for an arbitrary user-mode ETW provider — PerfView's
// "Any Stacks" view applied to a single provider.  The generic counterpart to all the
// keyword-specific stack tools: if an event class fires in the trace, this turns it into
// stack-rankable data.
//
// Use cases that don't fit any of the typed analyzers:
//   - AspNetCore: top stacks driving HTTP request handling
//   - Kestrel: top stacks issuing TLS handshakes
//   - EFCore: top stacks issuing SQL commands
//   - Antimalware-AMFilter: top stacks triggering AV scans
//   - any custom EventSource provider
//
// Filtering: by provider name (exact match against TraceEvent.ProviderName) and optionally
// by an event-name substring (matches TraceEvent.EventName, useful for "only count
// TaskScheduled events from System.Threading.Tasks.TplEventSource").
//
// Stack quality: the events must have been captured WITH stack walks enabled in the .wprp
// (a <Stacks> element listing the relevant Provider+Keyword).  Without it, every sample lands
// on a no-stack root and the top-N is just the leaf frame name — useful for confirming the
// provider fired but not for "which call chain caused this".
public static class GenericEventStackAnalysis
{
    public static GenericEventStacksResponse TopStacks(
        TraceLog trace,
        string providerName,
        string? eventNameSubstring,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        int whenBuckets = 0,
        bool? filterSpecified = null)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, whenBuckets);
        var req = new StackAnalysisRequest(pid, startUs, endUs, symbolLog, when)
        {
            FilterSpecified = filterSpecified,
        };
        var ctx = BuildNormalized(trace, providerName, eventNameSubstring, req);

        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = ctx.Normalized };
        var totalMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveMetric)
            .Take(top)
            .Select(n => new GenericEventStackRow(
                Function: n.Name,
                ExclusiveCount: (long)n.ExclusiveMetric,
                InclusiveCount: (long)n.InclusiveMetric,
                ExclusivePct: StackSourceTopN.Pct(totalMetric, n.ExclusiveMetric),
                InclusivePct: StackSourceTopN.Pct(totalMetric, n.InclusiveMetric),
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalCount, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(req.HasFilter, ctx.TraceTotalCount, n.InclusiveMetric)))
            .ToList();

        return new GenericEventStacksResponse(
            Rows: rows,
            ProviderName: providerName,
            EventNameSubstring: eventNameSubstring,
            TotalEventCount: ctx.TotalCount,
            TopEventNames: ctx.TopEventNames,
            Stats: ctx.Stats,
            Warnings: ctx.Warnings,
            When: when.Build());
    }

    public static CallerCalleeResponse CallerCallee(
        TraceLog trace,
        string providerName,
        string? eventNameSubstring,
        string focusFunction,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, 0);
        var req = new StackAnalysisRequest(pid, startUs, endUs, symbolLog, when);
        var ctx = BuildNormalized(trace, providerName, eventNameSubstring, req);
        return StackSourceTopN.ComputeCallerCallee(
            ctx.Normalized, focusFunction, top, metricName: "providerEvents", ctx.Stats, ctx.Warnings);
    }

    private record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalCount,
        long TotalCount,
        IReadOnlyList<GenericEventNameRow> TopEventNames,
        List<string> Warnings);

    private static BuildContext BuildNormalized(
        TraceLog trace,
        string providerName,
        string? eventNameSubstring,
        StackAnalysisRequest req)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(req.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalCount = 0;
        long totalCount = 0;
        var countByEventName = new Dictionary<string, long>(StringComparer.Ordinal);

        // Subscribe via AddCallbackForProviderEvents on both Dynamic and Registered parsers so
        // RejectProvider short-circuits non-matching providers at the dispatcher level — once
        // the filter says RejectProvider for some provider, we never see another event from it.
        // That turns a per-event string compare on every event in the trace into one compare
        // per unique provider, which is the difference between O(events) and O(providers).
        // Dynamic covers EventSource manifests; Registered covers TDH-resolvable providers
        // (.man / kernel-registered).  An event is dispatched through whichever parser owns
        // its template, so attaching to both is union not double-count.
        EventFilterResponse Filter(string provName, string evName)
        {
            if (!string.Equals(provName, providerName, StringComparison.Ordinal))
                return EventFilterResponse.RejectProvider;
            // OrdinalIgnoreCase (deviates from PerfView's case-sensitive Any Stacks) — friendlier
            // for LLM consumers who don't always know exact casing.  Reflected in the
            // `eventNameSubstring` Description on the MCP tool.
            if (eventNameSubstring is { Length: > 0 } sub &&
                evName.IndexOf(sub, StringComparison.OrdinalIgnoreCase) < 0)
                return EventFilterResponse.RejectEvent;
            return EventFilterResponse.AcceptEvent;
        }

        void Handle(TraceEvent data)
        {
            traceTotalCount++;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (!req.PassesFilter(data.ProcessID, nowUs)) return;

            totalCount++;
            countByEventName[data.EventName] = countByEventName.GetValueOrDefault(data.EventName) + 1;
            raw.AddSample(data.CallStackIndex(), data, 1);
            req.When.Add(nowUs, 1);
        }

        var source = trace.Events.GetSource();
        new DynamicTraceEventParser(source).AddCallbackForProviderEvents(Filter, Handle);
        new RegisteredTraceEventParser(source).AddCallbackForProviderEvents(Filter, Handle);
        source.Process();
        raw.Source.DoneAddingSamples();

        if (req.ResolveSymbols)
            raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead: false);

        var topEventNames = StackSourceTopN.TopByValue(countByEventName, 20, (k, v) => new GenericEventNameRow(k, v));

        var warnings = new List<string>();
        if (totalCount == 0)
            warnings.Add(
                $"No events matched provider '{providerName}'" +
                (eventNameSubstring is { Length: > 0 } s ? $" with event-name substring '{s}'" : "") +
                ".  The provider may not be enabled in the capture profile, or no matching " +
                "event fired in the filter window.  Use find_marker to confirm the provider " +
                "is present in the trace.");
        if (!req.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalCount, totalCount, topEventNames, warnings);
    }
}
