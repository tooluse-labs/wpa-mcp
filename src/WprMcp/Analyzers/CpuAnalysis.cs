using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

public static class CpuAnalysis
{
    // Symbol-stat / normalization / fold logic lives in StackSourceTopN — same pipeline
    // is reused by BlockedTimeStackAnalysis. PerfView-parity invariants (?!? root, raw-
    // before-normalize symbol resolution, module!? folding) are implemented there. If
    // you need to revalidate parity, see tests/manual/perfview_compare.md.

    public static CpuTopFunctionsResponse TopFunctions(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        bool excludeEtwSelfOverhead = false,
        bool includeTracePct = false)
    {
        var hasFilter = pid.HasValue || startUs.HasValue || endUs.HasValue;
        var (normalized, stats, traceTotalSamples) = BuildNormalized(
            trace, pid, startUs, endUs, symbolLog, excludeEtwSelfOverhead, includeTracePct);

        return BuildTopFunctionsResponse(
            normalized,
            stats,
            traceTotalSamples,
            top,
            hasFilter,
            includeTracePct);
    }

    public static IReadOnlyDictionary<int, CpuTopFunctionsResponse> TopFunctionsMultiPid(
        TraceLog trace,
        int top,
        IReadOnlyCollection<int> pids,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        bool excludeEtwSelfOverhead = false,
        bool includeTracePct = false,
        ICollection<string>? warnings = null)
    {
        var distinctPids = pids.Distinct().ToArray();
        var rawByPid = distinctPids.ToDictionary(pid => pid, _ => StackSourceTopN.CreateRawSource(trace));
        long traceTotalSamples = 0;

        foreach (var ev in trace.Events)
        {
            var usSinceStart = (long)(ev.TimeStampRelativeMSec * 1000);
            if (!includeTracePct && endUs is { } eUsForBreak && usSinceStart >= eUsForBreak) break;

            if (ev is not SampledProfileTraceData) continue;
            if (includeTracePct) traceTotalSamples++;
            if (startUs is { } s && usSinceStart < s) continue;
            if (endUs is { } eUs && usSinceStart >= eUs) continue;
            if (rawByPid.TryGetValue(ev.ProcessID, out var raw))
                raw.AddSample(ev.CallStackIndex(), ev, metric: 1);
        }

        using var symbolReader = StackSourceTopN.OpenSymbolReader(symbolLog);
        return BuildTopFunctionsResponsesForRawSources(
            trace,
            rawByPid,
            symbolReader,
            traceTotalSamples,
            top,
            excludeEtwSelfOverhead,
            hasFilter: true,
            includeTracePct,
            warnings);
    }

    internal static IReadOnlyDictionary<int, CpuTopFunctionsResponse> BuildTopFunctionsResponsesForRawSources(
        TraceLog trace,
        IReadOnlyDictionary<int, StackSourceTopN.RawStackSource> rawByPid,
        Microsoft.Diagnostics.Symbols.SymbolReader symbolReader,
        long traceTotalSamples,
        int top,
        bool excludeEtwSelfOverhead,
        bool hasFilter,
        bool includeTracePct,
        ICollection<string>? warnings = null,
        Func<int, StackSourceTopN.RawStackSource, CpuTopFunctionsResponse>? project = null)
    {
        var result = new Dictionary<int, CpuTopFunctionsResponse>();
        foreach (var (pid, raw) in rawByPid)
        {
            try
            {
                result[pid] = project?.Invoke(pid, raw) ?? BuildTopFunctionsResponseForRawSource(
                    trace,
                    raw,
                    symbolReader,
                    traceTotalSamples,
                    top,
                    excludeEtwSelfOverhead,
                    hasFilter,
                    includeTracePct);
            }
            catch (Exception ex)
            {
                warnings?.Add($"pid {pid}: {ex.Message}");
            }
        }

        return result;
    }

    internal static CpuTopFunctionsResponse BuildTopFunctionsResponseForRawSource(
        TraceLog trace,
        StackSourceTopN.RawStackSource raw,
        Microsoft.Diagnostics.Symbols.SymbolReader symbolReader,
        long traceTotalSamples,
        int top,
        bool excludeEtwSelfOverhead,
        bool hasFilter,
        bool includeTracePct)
    {
        raw.Source.DoneAddingSamples();
        raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead);
        return BuildTopFunctionsResponse(
            normalized,
            stats,
            traceTotalSamples,
            top,
            hasFilter,
            includeTracePct);
    }

    private static CpuTopFunctionsResponse BuildTopFunctionsResponse(
        MutableTraceEventStackSource normalized,
        SymbolStats stats,
        long traceTotalSamples,
        int top,
        bool hasFilter,
        bool includeTracePct)
    {
        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = normalized };
        var totalSamples = (double)Math.Max(1, callTree.Root.InclusiveCount);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveCount)
            .Take(top)
            .Select(n => new CpuFunctionRow(
                Function: n.Name,
                ExclusiveSamples: (long)n.ExclusiveCount,
                InclusiveSamples: (long)n.InclusiveCount,
                ExclusivePct: 100.0 * n.ExclusiveCount / totalSamples,
                InclusivePct: 100.0 * n.InclusiveCount / totalSamples,
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, traceTotalSamples, n.ExclusiveCount),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(hasFilter, traceTotalSamples, n.InclusiveCount)))
            .ToList();

        var warnings = stats.ResolutionRate < 0.8
            ? new List<string> { WarningBuilder.SymbolResolution(stats.ResolutionRate) }
            : new List<string>();
        if (hasFilter && !includeTracePct)
        {
            warnings.Add("PctOfTrace omitted; pass includeTracePct=true to compute it (slow on large ETLs).");
        }

        return new CpuTopFunctionsResponse(rows, stats, warnings);
    }

    public static CallerCalleeResponse CallerCallee(
        TraceLog trace,
        string focusFunction,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        bool excludeEtwSelfOverhead = false)
    {
        var (normalized, stats, _) = BuildNormalized(
            trace, pid, startUs, endUs, symbolLog, excludeEtwSelfOverhead, countTraceTotalSamples: false);
        var baseWarnings = stats.ResolutionRate < 0.8
            ? new List<string> { WarningBuilder.SymbolResolution(stats.ResolutionRate) }
            : new List<string>();

        return StackSourceTopN.ComputeCallerCallee(
            normalized, focusFunction, top, metricName: "samples", stats, baseWarnings);
    }

    /// <summary>
    /// Walk SampledProfileTraceData events, optionally tally trace-total for PctOfTrace,
    /// push samples (metric=1) into the stack source for events passing the pid/window
    /// filter, then run LookupWarmSymbols + ComputeSymbolStats + BuildNormalized. Shared by TopFunctions and
    /// CallerCallee — same input semantics, just different terminal projections.
    /// </summary>
    private static (MutableTraceEventStackSource Normalized, SymbolStats Stats, long TraceTotalSamples)
        BuildNormalized(
            TraceLog trace,
            int? pid,
            long? startUs,
            long? endUs,
            TextWriter symbolLog,
            bool excludeEtwSelfOverhead,
            bool countTraceTotalSamples)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(symbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalSamples = 0;
        foreach (var ev in trace.Events)
        {
            var usSinceStart = (long)(ev.TimeStampRelativeMSec * 1000);
            if (!countTraceTotalSamples && endUs is { } eUsForBreak && usSinceStart >= eUsForBreak) break;

            if (ev is not SampledProfileTraceData) continue;
            if (countTraceTotalSamples) traceTotalSamples++;
            if (pid is { } p && ev.ProcessID != p) continue;
            if (startUs is { } s && usSinceStart < s) continue;
            if (endUs is { } eUs && usSinceStart >= eUs) continue;

            raw.AddSample(ev.CallStackIndex(), ev, metric: 1);
        }
        raw.Source.DoneAddingSamples();

        raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead);
        return (normalized, stats, traceTotalSamples);
    }
}
