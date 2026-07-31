using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

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
        var scope = ResolveLegacyScope(trace, pid, startUs, endUs);
        return TopFunctions(
            trace,
            top,
            scope,
            symbolLog,
            excludeEtwSelfOverhead,
            includeTracePct,
            resolveSymbols,
            hasFilter);
    }

    internal static CpuTopFunctionsResponse TopFunctions(
        TraceLog trace,
        int top,
        ThreadAnalysisScope scope,
        TextWriter symbolLog,
        bool excludeEtwSelfOverhead = false,
        bool includeTracePct = false,
        bool resolveSymbols = false,
        bool? hasFilter = null) =>
        TopFunctions(
            trace,
            top,
            scope,
            symbolLog,
            excludeEtwSelfOverhead,
            includeTracePct,
            resolveSymbols,
            hasFilter ?? HasScopeFilter(trace, scope));

    private static CpuTopFunctionsResponse TopFunctions(
        TraceLog trace,
        int top,
        ThreadAnalysisScope scope,
        TextWriter symbolLog,
        bool excludeEtwSelfOverhead,
        bool includeTracePct,
        bool resolveSymbols,
        bool hasFilter)
    {
        var (normalized, stats, traceTotalSamples, filteredSamples, hasSampledProfileStacks) = BuildNormalized(
            trace,
            scope,
            symbolLog,
            excludeEtwSelfOverhead,
            includeTracePct,
            resolveSymbols);

        return BuildTopFunctionsResponse(
            normalized,
            stats,
            traceTotalSamples,
            top,
            hasFilter,
            includeTracePct,
            resolveSymbols,
            filteredSamples,
            scope,
            hasSampledProfileStacks);
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
        var window = Validation.RequireWindowInput(startUs, endUs).Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds),
            maxDurationUs: null);
        long traceTotalSamples = 0;
        var started = Stopwatch.GetTimestamp();
        var scanCompleted = true;
        var eventCount = 0;
        var pidsWithSampledProfileStacks = new HashSet<int>();

        foreach (var ev in trace.Events)
        {
            if ((++eventCount & 0x3fff) == 0 && BudgetExceeded(started, timeBudgetMs))
            {
                scanCompleted = false;
                break;
            }

            var usSinceStart = TraceTime.FromMilliseconds(ev.TimeStampRelativeMSec);
            if (!includeTracePct && usSinceStart >= window.EndUs) break;

            if (ev is not SampledProfileTraceData) continue;
            if (includeTracePct) traceTotalSamples++;
            if (!window.ContainsPoint(usSinceStart)) continue;
            if (rawByPid.TryGetValue(ev.ProcessID, out var raw))
            {
                raw.AddSample(ev.CallStackIndex(), ev, metric: 1);
                if (ev.CallStackIndex() != CallStackIndex.Invalid)
                    pidsWithSampledProfileStacks.Add(ev.ProcessID);
            }
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
            skippedPids: skippedPids,
            pidsWithSampledProfileStacks: pidsWithSampledProfileStacks);

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
        ICollection<int>? skippedPids = null,
        IReadOnlySet<int>? pidsWithSampledProfileStacks = null)
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
                    resolveSymbols,
                    hasSampledProfileStacks:
                        pidsWithSampledProfileStacks?.Contains(pid) == true);
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
        bool resolveSymbols = true,
        bool hasSampledProfileStacks = false)
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
            resolveSymbols,
            hasSampledProfileStacks: hasSampledProfileStacks);
    }

    private static CpuTopFunctionsResponse BuildTopFunctionsResponse(
        MutableTraceEventStackSource normalized,
        SymbolStats stats,
        long traceTotalSamples,
        int top,
        bool hasFilter,
        bool includeTracePct,
        bool resolveSymbols,
        long? filteredSamples = null,
        ThreadAnalysisScope? scope = null,
        bool hasSampledProfileStacks = false)
    {
        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = normalized };
        var sourceTotalSamples = filteredSamples ?? (long)callTree.Root.InclusiveCount;
        var totalSamples = (double)Math.Max(1, sourceTotalSamples);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveCount)
            .Take(top)
            .Select(n => new CpuFunctionRow(
                Function: n.Name,
                ExclusiveSamples: (long)n.ExclusiveCount,
                InclusiveSamples: (long)n.InclusiveCount,
                ExclusivePct: StackSourceTopN.Pct(totalSamples, n.ExclusiveCount),
                InclusivePct: StackSourceTopN.Pct(totalSamples, n.InclusiveCount),
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
        if (scope?.PidReuseObserved == true)
        {
            warnings.Add(
                "ambiguous_process_instance: pid-only scope aggregates multiple process lifetimes.");
        }

        return new CpuTopFunctionsResponse(
            rows,
            stats,
            warnings,
            TotalSamples: sourceTotalSamples,
            SelectedProcess: scope?.Process?.Key,
            SelectedThread: scope?.Thread?.Key,
            HasSampledProfileStacks: hasSampledProfileStacks,
            SymbolResolutionState: StackSourceTopN.GetSymbolResolutionState(
                resolveSymbols, stats, hasSampledProfileStacks));
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
        var scope = ResolveLegacyScope(trace, pid, startUs, endUs);
        return CallerCallee(
            trace,
            focusFunction,
            top,
            scope,
            symbolLog,
            excludeEtwSelfOverhead,
            resolveSymbols);
    }

    internal static CallerCalleeResponse CallerCallee(
        TraceLog trace,
        string focusFunction,
        int top,
        ThreadAnalysisScope scope,
        TextWriter symbolLog,
        bool excludeEtwSelfOverhead = false,
        bool resolveSymbols = false)
    {
        var (normalized, stats, _, filteredSamples, hasSampledProfileStacks) = BuildNormalized(
            trace,
            scope,
            symbolLog,
            excludeEtwSelfOverhead,
            countTraceTotalSamples: false,
            resolveSymbols);
        var baseWarnings = !resolveSymbols
            ? new List<string> { WarningBuilder.SymbolResolutionSkipped("cpu_caller_callee") }
            : stats.ResolutionRate < 0.8
                ? new List<string> { WarningBuilder.SymbolResolution(stats.ResolutionRate) }
                : new List<string>();
        if (scope.PidReuseObserved)
        {
            baseWarnings.Add(
                "ambiguous_process_instance: pid-only scope aggregates multiple process lifetimes.");
        }

        return StackSourceTopN.ComputeCallerCallee(
            normalized,
            focusFunction,
            top,
            metricName: "samples",
            stats,
            baseWarnings,
            sourceTotalMetric: filteredSamples,
            selectedProcess: scope.Process?.Key,
            selectedThread: scope.Thread?.Key,
            hasSampledProfileStacks: hasSampledProfileStacks,
            symbolResolutionState: StackSourceTopN.GetSymbolResolutionState(
                resolveSymbols, stats, hasSampledProfileStacks));
    }

    /// <summary>
    /// Walk SampledProfileTraceData events, optionally tally trace-total for PctOfTrace,
    /// push samples (metric=1) into the stack source for events passing the pid/window
    /// filter, then run LookupWarmSymbols + ComputeSymbolStats + BuildNormalized. Shared by TopFunctions and
    /// CallerCallee — same input semantics, just different terminal projections.
    /// </summary>
    private static (
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalSamples,
        long FilteredSamples,
        bool HasSampledProfileStacks)
        BuildNormalized(
            TraceLog trace,
            ThreadAnalysisScope scope,
            TextWriter symbolLog,
            bool excludeEtwSelfOverhead,
            bool countTraceTotalSamples,
            bool resolveSymbols)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(symbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalSamples = 0;
        long filteredSamples = 0;
        var hasSampledProfileStacks = false;
        foreach (var ev in trace.Events)
        {
            var usSinceStart = TraceTime.FromMilliseconds(ev.TimeStampRelativeMSec);
            if (!countTraceTotalSamples && usSinceStart >= scope.Window.EndUs) break;

            if (ev is not SampledProfileTraceData) continue;
            if (countTraceTotalSamples) traceTotalSamples++;
            if (!PassesScope(scope, ev.ProcessID, ev.ThreadID, usSinceStart)) continue;

            filteredSamples++;
            if (ev.CallStackIndex() != CallStackIndex.Invalid)
                hasSampledProfileStacks = true;
            raw.AddSample(ev.CallStackIndex(), ev, metric: 1);
        }
        raw.Source.DoneAddingSamples();

        if (resolveSymbols)
            raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead);
        return (
            normalized,
            stats,
            traceTotalSamples,
            filteredSamples,
            hasSampledProfileStacks);
    }

    internal static bool PassesScope(
        ThreadAnalysisScope scope,
        int pid,
        int tid,
        long timestampUs) =>
        scope.MatchesPoint(pid, tid, timestampUs);

    private static ThreadAnalysisScope ResolveLegacyScope(
        TraceLog trace,
        int? pid,
        long? startUs,
        long? endUs)
    {
        var window = Validation.RequireWindowInput(startUs, endUs).Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds),
            maxDurationUs: null);
        var resolution = ThreadAnalysisScope.Resolve(
            window,
            pid,
            tid: null,
            processStartUs: null,
            threadStartUs: null,
            TraceIdentityIndex.For(trace));
        return resolution.Status == InstanceResolutionStatus.Resolved &&
               resolution.Value.HasValue
            ? resolution.Value.Value
            : throw new InvalidOperationException(
                $"Unable to resolve sampled CPU scope: {resolution.Status}.");
    }

    private static bool HasScopeFilter(TraceLog trace, ThreadAnalysisScope scope)
    {
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        return scope.Pid.HasValue ||
               scope.Window.StartUs != 0 ||
               scope.Window.EndUs != traceEndUs;
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
