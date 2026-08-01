using System.Globalization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

internal static class TraceMetadataAnalysis
{
    private const int MaxProviderRows = 50;
    private const int MaxDriverRows = 50;

    public static TraceMetadata Analyze(TraceLog trace, TraceCapabilities capabilities)
    {
        var system = BuildSystemConfiguration(trace);
        var drivers = BuildDriverSummary(trace);
        var eventSummary = BuildEventSummary(trace, capabilities);
        var limitations = BuildLimitations(system, drivers);

        return new TraceMetadata(
            System: system,
            Stackwalks: eventSummary.Stackwalks,
            ProviderEvents: eventSummary.ProviderEvents,
            Drivers: drivers,
            Limitations: limitations);
    }

    private static TraceSystemConfiguration BuildSystemConfiguration(TraceLog trace)
    {
        var source = trace.Events.GetSource();
        return new TraceSystemConfiguration(
            MachineName: NullIfEmpty(trace.MachineName),
            OsName: NullIfEmpty(trace.OSName),
            OsBuild: NullIfEmpty(trace.OSBuild),
            OsVersion: NullIfEmpty(source.OSVersion?.ToString()),
            ProcessorCount: PositiveOrNull(source.NumberOfProcessors),
            CpuSpeedMhz: PositiveOrNull(source.CpuSpeedMHz),
            CpuModel: null,
            BootTimeUtc: trace.BootTime == DateTime.MinValue
                ? null
                : trace.BootTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            UtcOffsetMinutes: trace.UTCOffsetMinutes,
            MetadataSource: "TraceLog/TraceEventSource");
    }

    private static (TraceStackwalkSummary Stackwalks, ProviderEventCountSummary ProviderEvents) BuildEventSummary(
        TraceLog trace,
        TraceCapabilities capabilities)
    {
        var providers = new Dictionary<string, ProviderAccumulator>(StringComparer.OrdinalIgnoreCase);
        long totalEvents = 0;
        long eventsWithCallStacks = 0;

        foreach (var ev in trace.Events)
        {
            totalEvents++;

            var provider = NullIfEmpty(ev.ProviderName) ?? "<unknown>";
            if (!providers.TryGetValue(provider, out var accumulator))
            {
                accumulator = new ProviderAccumulator(provider);
                providers.Add(provider, accumulator);
            }

            var hasCallStack = trace.GetCallStackIndexForEvent(ev) != CallStackIndex.Invalid;
            if (hasCallStack) eventsWithCallStacks++;
            accumulator.EventCount++;
            if (hasCallStack) accumulator.EventsWithCallStacks++;
        }

        var topProviders = providers.Values
            .OrderByDescending(provider => provider.EventCount)
            .ThenBy(provider => provider.Provider, StringComparer.OrdinalIgnoreCase)
            .Take(MaxProviderRows)
            .Select(provider => new ProviderEventCount(
                Provider: provider.Provider,
                EventCount: provider.EventCount,
                EventsWithCallStacks: provider.EventsWithCallStacks,
                StackCoveragePct: RatioOrNull(provider.EventsWithCallStacks, provider.EventCount),
                StackCoveragePercent: PercentOrNull(provider.EventsWithCallStacks, provider.EventCount)))
            .ToList();

        var topProviderEventCount = topProviders.Sum(provider => provider.EventCount);
        var providerSummary = new ProviderEventCountSummary(
            TotalProviderCount: providers.Count,
            TotalEventCount: totalEvents,
            OtherEventCount: Math.Max(0, totalEvents - topProviderEventCount),
            TopProviders: topProviders);

        var stackwalkSummary = new TraceStackwalkSummary(
            HasStackWalkEvents: capabilities.HasExplicitStackWalkEvents,
            StackWalkEventCount: capabilities.ExplicitStackWalkEventCount,
            EventsWithCallStacks: eventsWithCallStacks,
            EventStackCoveragePct: RatioOrNull(eventsWithCallStacks, totalEvents),
            HasExplicitStackWalkEvents: capabilities.HasExplicitStackWalkEvents,
            HasUsableEventStacks: capabilities.HasAttachedEventStacks,
            EventStackCoveragePercent: PercentOrNull(eventsWithCallStacks, totalEvents));

        return (stackwalkSummary, providerSummary);
    }

    private static DriverModuleSummary BuildDriverSummary(TraceLog trace)
    {
        var driverModules = trace.ModuleFiles
            .Where(IsDriverModule)
            .GroupBy(module => ModuleIdentity(module), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(module => ModuleName(module), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var topDrivers = driverModules
            .Take(MaxDriverRows)
            .Select(module => new TraceDriverModule(
                Module: ModuleName(module),
                Path: NullIfEmpty(module.FilePath) ?? string.Empty,
                ImageSizeBytes: ToLongSaturating(module.ImageSize),
                FileVersion: NullIfEmpty(module.FileVersion?.ToString()),
                ProductName: NullIfEmpty(module.ProductName),
                ProductVersion: NullIfEmpty(module.ProductVersion)))
            .ToList();

        return new DriverModuleSummary(driverModules.Count, topDrivers);
    }

    private static IReadOnlyList<string> BuildLimitations(
        TraceSystemConfiguration system,
        DriverModuleSummary drivers)
    {
        var limitations = new List<string>();

        limitations.Add(
            "event_count_representation=tracelog_etlx_materialized_logical_events;raw_etw_record_count=not_measured;parser_coverage=not_computed;TraceLog materialization can fold or transform records, so do not interpret an external-raw/count ratio as parser loss");

        if (system.CpuModel is null)
        {
            limitations.Add(
                "cpu_model_not_available_from_trace_metadata; inspect_trace reports CPU count/speed only and does not fall back to the host machine");
        }

        if (drivers.TotalDriverModuleCount == 0)
            limitations.Add("driver_modules_not_observed_in_trace_module_table");

        return limitations;
    }

    private static bool IsDriverModule(TraceModuleFile module) =>
        EndsWithSys(module.Name) || EndsWithSys(module.FilePath);

    private static string ModuleIdentity(TraceModuleFile module) =>
        NullIfEmpty(module.FilePath) ?? ModuleName(module);

    private static string ModuleName(TraceModuleFile module) =>
        NullIfEmpty(module.Name) ??
        NullIfEmpty(SafeFileName(module.FilePath)) ??
        "<unknown>";

    private static string? SafeFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static bool EndsWithSys(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.EndsWith(".sys", StringComparison.OrdinalIgnoreCase);

    private static int? PositiveOrNull(int value) => value > 0 ? value : null;

    private static long ToLongSaturating(long value) => Math.Max(0, value);

    private static double? RatioOrNull(long numerator, long denominator) =>
        denominator == 0 ? null : numerator / (double)denominator;

    private static double? PercentOrNull(long numerator, long denominator) =>
        denominator == 0 ? null : 100.0 * numerator / denominator;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class ProviderAccumulator(string provider)
    {
        public string Provider { get; } = provider;
        public long EventCount { get; set; }
        public long EventsWithCallStacks { get; set; }
    }
}
