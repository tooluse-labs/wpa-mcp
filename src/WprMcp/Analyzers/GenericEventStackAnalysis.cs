using Microsoft.Diagnostics.Tracing.Etlx;
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
        int whenBuckets = 0)
    {
        var hasFilter = pid.HasValue || startUs.HasValue || endUs.HasValue;
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, whenBuckets);
        var ctx = BuildNormalized(trace, providerName, eventNameSubstring, pid, startUs, endUs, symbolLog, when);

        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = ctx.Normalized };
        var totalMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveMetric)
            .Take(top)
            .Select(n => new GenericEventStackRow(
                Function: n.Name,
                ExclusiveCount: (long)n.ExclusiveMetric,
                InclusiveCount: (long)n.InclusiveMetric,
                ExclusivePct: 100.0 * n.ExclusiveMetric / totalMetric,
                InclusivePct: 100.0 * n.InclusiveMetric / totalMetric,
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, ctx.TraceTotalCount, n.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, ctx.TraceTotalCount, n.InclusiveMetric)))
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
        var ctx = BuildNormalized(trace, providerName, eventNameSubstring, pid, startUs, endUs, symbolLog, when);
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
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        StackSourceTopN.WhenHistogram when)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(symbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalCount = 0;
        long totalCount = 0;
        var countByEventName = new Dictionary<string, long>(StringComparer.Ordinal);

        var source = trace.Events.GetSource();
        source.AllEvents += data =>
        {
            // Hot path: provider-name string compare first, before any other work.  The
            // ProviderName property reads from a cached lookup; cheap on the second hit per
            // provider.
            if (!string.Equals(data.ProviderName, providerName, StringComparison.Ordinal)) return;
            if (eventNameSubstring is { Length: > 0 } sub &&
                data.EventName.IndexOf(sub, StringComparison.OrdinalIgnoreCase) < 0) return;

            traceTotalCount++;
            var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
            if (pid is { } p && data.ProcessID != p) return;
            if (startUs is { } s && nowUs < s) return;
            if (endUs is { } e && nowUs > e) return;

            totalCount++;
            countByEventName[data.EventName] = countByEventName.GetValueOrDefault(data.EventName) + 1;
            raw.AddSample(data.CallStackIndex(), data, 1);
            when.Add(nowUs, 1);
        };
        source.Process();
        raw.Source.DoneAddingSamples();

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
        if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return new BuildContext(normalized, stats, traceTotalCount, totalCount, topEventNames, warnings);
    }
}
