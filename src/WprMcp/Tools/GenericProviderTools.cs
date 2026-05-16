using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class GenericProviderTools
{
    private readonly TraceCache _cache;
    public GenericProviderTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Top-N call stacks ranked by event count for ANY user-mode ETW provider — PerfView's " +
        "'Any Stacks' view applied to a single provider.  Use this when you need stack-rankable " +
        "data from a provider that doesn't have a dedicated tool: AspNetCore, Kestrel, EFCore, " +
        "Microsoft-Antimalware-AMFilter, Microsoft-Windows-Sense (Defender for Endpoint), or " +
        "any custom EventSource.  First call find_marker to identify which providers are in " +
        "the trace, then plug the exact ProviderName here.  Optional eventNameSubstring narrows " +
        "to a specific event class (e.g., 'TaskScheduled' for TplEventSource).  Stack quality " +
        "depends on whether stack-walks were enabled for this provider in the .wprp; without " +
        "them, every sample lands on the leaf frame and the view confirms the provider fired " +
        "but doesn't trace the call chain.")]
    public GenericEventStacksResponse GenericEventTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Exact provider name (e.g., 'Microsoft-AspNetCore-Hosting')")] string providerName,
        [Description("Optional event-name substring (case-insensitive). Empty / null = all events from the provider.")] string? eventNameSubstring = null,
        [Description("Top N stacks by exclusive event count (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Number of equal-width buckets for the time histogram (0 = disabled)")] int whenBuckets = 0,
        [Description(StackResponseOptions.CompactStacksDescription)]
        bool compactStacks = false,
        [Description(StackResponseOptions.SummaryOnlyDescription)]
        bool summaryOnly = false)
    {
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        Validation.RequireProviderName(providerName);
        var trace = _cache.Get(path);
        return GenericEventStackAnalysis.TopStacks(
            trace, providerName, eventNameSubstring, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid, startUs, endUs, Console.Error, whenBuckets);
    }

    [McpServerTool, Description(
        "Caller-callee drill-down on a focus frame in a generic provider's stack source.  " +
        "Same provider + event-name filtering as generic_event_top_stacks.  Metric is event " +
        "count; top-N callers ranked by inclusive count flowing INTO focus, callees by count OUT.")]
    public CallerCalleeResponse GenericEventCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Exact provider name")] string providerName,
        [Description("Focus function name (substring or exact)")] string focusFunction,
        [Description("Optional event-name substring. Empty / null = all events from the provider.")] string? eventNameSubstring = null,
        [Description("Top N callers / callees (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(focusFunction);
        Validation.RequireProviderName(providerName);
        var trace = _cache.Get(path);
        return GenericEventStackAnalysis.CallerCallee(trace, providerName, eventNameSubstring, focusFunction, top, pid, startUs, endUs, Console.Error);
    }
}
