using System.Diagnostics;
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
        bool includeTracePct = false,
        bool resolveSymbols = false)
    {
        var hasFilter = pid.HasValue || startUs.HasValue || endUs.HasValue;
        var (normalized, stats, traceTotalSamples) = BuildNormalized(
            trace, pid, startUs, endUs, symbolLog, excludeEtwSelfOverhead, includeTracePct, resolveSymbols);

        return BuildTopFunctionsResponse(
            normalized,
            stats,
            traceTotalSamples,
            top,
            hasFilter,
            includeTracePct,
            resolveSymbols);
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
        ICollection<string>? warnings = null,
        bool resolveSymbols = false,
        int? timeBudgetMs = null,
        ICollection<int>? skippedPids = null)
    {
        var distinctPids = pids.Distinct().ToArray();
        var rawByPid = distinctPids.ToDictionary(pid => pid, _ => StackSourceTopN.CreateRawSource(trace));
        long traceTotalSamples = 0;
        var started = Stopwatch.GetTimestamp();
        var scanCompleted = true;
        var eventCount = 0;

        foreach (var ev in trace.Events)
        {
            if ((++eventCount & 0x3fff) == 0 && BudgetExceeded(started, timeBudgetMs))
            {
                scanCompleted = false;
                break;
            }

            var usSinceStart = (long)(ev.TimeStampRelativeMSec * 1000);
            if (!includeTracePct && endUs is { } eUsForBreak && usSinceStart >= eUsForBreak) break;

            if (ev is not SampledProfileTraceData) continue;
            if (includeTracePct) traceTotalSamples++;
            if (startUs is { } s && usSinceStart < s) continue;
            if (endUs is { } eUs && usSinceStart >= eUs) continue;
            if (rawByPid.TryGetValue(ev.ProcessID, out var raw))
                raw.AddSample(ev.CallStackIndex(), ev, metric: 1);
        }

        if (!scanCompleted)
        {
            foreach (var pid in distinctPids)
                skippedPids?.Add(pid);
            warnings?.Add(TimeBudgetWarning(timeBudgetMs, completed: 0, requested: distinctPids.Length, skippedPids));
            return new Dictionary<int, CpuTopFunctionsResponse>();
        }

        using var symbolReader = StackSourceTopN.OpenSymbolReader(symbolLog);
        var result = BuildTopFunctionsResponsesForRawSources(
            trace,
            rawByPid,
            symbolReader,
            traceTotalSamples,
            top,
            excludeEtwSelfOverhead,
            hasFilter: true,
            includeTracePct,
            warnings,
            resolveSymbols: resolveSymbols,
            shouldStop: () => BudgetExceeded(started, timeBudgetMs),
            skippedPids: skippedPids);

        if (!resolveSymbols)
        {
            warnings?.Add(
                "Symbol resolution skipped for cpu_top_functions_batch fast mode; pass resolveSymbols=true for warmer function names after narrowing the PID set.");
        }

        if (skippedPids is { Count: > 0 })
            warnings?.Add(TimeBudgetWarning(timeBudgetMs, result.Count, distinctPids.Length, skippedPids));

        return result;
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
        Func<int, StackSourceTopN.RawStackSource, CpuTopFunctionsResponse>? project = null,
        bool resolveSymbols = true,
        Func<bool>? shouldStop = null,
        ICollection<int>? skippedPids = null)
    {
        var result = new Dictionary<int, CpuTopFunctionsResponse>();
        foreach (var (pid, raw) in rawByPid)
        {
            if (shouldStop?.Invoke() == true)
            {
                skippedPids?.Add(pid);
                continue;
            }

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
                    includeTracePct,
                    resolveSymbols);
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
        bool includeTracePct,
        bool resolveSymbols = true)
    {
        raw.Source.DoneAddingSamples();
        if (resolveSymbols)
            raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead);
        return BuildTopFunctionsResponse(
            normalized,
            stats,
            traceTotalSamples,
            top,
            hasFilter,
            includeTracePct,
            resolveSymbols);
    }

    private static CpuTopFunctionsResponse BuildTopFunctionsResponse(
        MutableTraceEventStackSource normalized,
        SymbolStats stats,
        long traceTotalSamples,
        int top,
        bool hasFilter,
        bool includeTracePct,
        bool resolveSymbols)
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

        var warnings = !resolveSymbols
            ? new List<string> { WarningBuilder.SymbolResolutionSkipped("cpu_top_functions") }
            : stats.ResolutionRate < 0.8
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
        bool excludeEtwSelfOverhead = false,
        bool resolveSymbols = false)
    {
        var (normalized, stats, _) = BuildNormalized(
            trace, pid, startUs, endUs, symbolLog, excludeEtwSelfOverhead, countTraceTotalSamples: false, resolveSymbols);
        var baseWarnings = !resolveSymbols
            ? new List<string> { WarningBuilder.SymbolResolutionSkipped("cpu_caller_callee") }
            : stats.ResolutionRate < 0.8
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
            bool countTraceTotalSamples,
            bool resolveSymbols)
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

        if (resolveSymbols)
            raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead);
        return (normalized, stats, traceTotalSamples);
    }

    private static bool BudgetExceeded(long startedTimestamp, int? timeBudgetMs)
        => timeBudgetMs is { } budget
           && (Stopwatch.GetTimestamp() - startedTimestamp) * 1000.0 / Stopwatch.Frequency >= budget;

    private static string TimeBudgetWarning(
        int? timeBudgetMs,
        int completed,
        int requested,
        ICollection<int>? skippedPids)
    {
        var skippedText = skippedPids is { Count: > 0 }
            ? $" Skipped PIDs: {string.Join(", ", skippedPids)}."
            : "";
        return $"time_budget_exhausted: cpu_top_functions_batch reached its {timeBudgetMs ?? 0} ms soft budget after completing {completed}/{requested} PIDs.{skippedText} Returned evidence is partial; rerun with fewer PIDs, a narrower time window, or resolveSymbols=false.";
    }
}
