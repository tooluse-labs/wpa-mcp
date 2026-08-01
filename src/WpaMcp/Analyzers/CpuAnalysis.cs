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

    internal readonly record struct BatchSelector(int Pid, long? ProcessStartUs);

    internal sealed record BatchScope(BatchSelector Selector, ProcessAnalysisScope Scope);

    internal sealed record BatchExecution(
        IReadOnlyDictionary<int, CpuTopFunctionsResponse> PerPid,
        IReadOnlyList<string> Warnings,
        bool Partial,
        IReadOnlyList<int> SkippedPids,
        IReadOnlyList<int> CompletedPids,
        IReadOnlyList<int> PidsNotFound,
        IReadOnlyList<int> PidsWithNoSamples,
        IReadOnlyList<CpuBatchScopeResult> ScopeResults);

    public static CpuTopFunctionsResponse TopFunctions(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        bool excludeEtwSelfOverhead = false,
        bool includeTracePct = false,
        bool resolveSymbols = false,
        bool? traceHasCpuSamples = null)
    {
        var hasFilter = pid.HasValue || startUs.HasValue || endUs.HasValue;
        var scope = ResolveLegacyScope(trace, pid, startUs, endUs);
        var processScope = ProcessAnalysisScope.Resolve(
            scope.Window,
            pid,
            processStartUs: null,
            TraceIdentityIndex.For(trace));
        return TopFunctions(
            trace,
            top,
            scope,
            symbolLog,
            excludeEtwSelfOverhead,
            includeTracePct,
            resolveSymbols,
            hasFilter,
            processScope,
            traceHasCpuSamples);
    }

    internal static CpuTopFunctionsResponse TopFunctions(
        TraceLog trace,
        int top,
        ThreadAnalysisScope scope,
        TextWriter symbolLog,
        bool excludeEtwSelfOverhead = false,
        bool includeTracePct = false,
        bool resolveSymbols = false,
        bool? hasFilter = null,
        ProcessAnalysisScope? processScope = null,
        bool? traceHasCpuSamples = null) =>
        TopFunctions(
            trace,
            top,
            scope,
            symbolLog,
            excludeEtwSelfOverhead,
            includeTracePct,
            resolveSymbols,
            hasFilter ?? HasScopeFilter(trace, scope),
            processScope,
            traceHasCpuSamples);

    private static CpuTopFunctionsResponse TopFunctions(
        TraceLog trace,
        int top,
        ThreadAnalysisScope scope,
        TextWriter symbolLog,
        bool excludeEtwSelfOverhead,
        bool includeTracePct,
        bool resolveSymbols,
        bool hasFilter,
        ProcessAnalysisScope? processScope,
        bool? traceHasCpuSamples)
    {
        var (normalized, stats, traceTotalSamples, filteredSamples, hasSampledProfileStacks, stackCoverage) = BuildNormalized(
            trace,
            scope,
            symbolLog,
            excludeEtwSelfOverhead,
            includeTracePct,
            resolveSymbols,
            processScope);

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
            hasSampledProfileStacks,
            stackCoverage,
            processScope,
            traceHasCpuSamples);
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
        ICollection<int>? skippedPids = null,
        bool? traceHasCpuSamples = null)
    {
        var window = Validation.RequireWindowInput(startUs, endUs).Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds),
            maxDurationUs: null);
        var selectors = NormalizeBatchSelectors(pids.ToArray(), processStartUs: null);
        var execution = ExecuteTopFunctionsBatch(
            trace,
            top,
            selectors,
            window,
            symbolLog,
            excludeEtwSelfOverhead,
            includeTracePct,
            resolveSymbols,
            timeBudgetMs,
            traceHasCpuSamples: traceHasCpuSamples);
        foreach (var warning in execution.Warnings)
            warnings?.Add(warning);
        foreach (var pid in execution.SkippedPids)
            skippedPids?.Add(pid);
        return execution.PerPid;
    }

    internal static IReadOnlyList<BatchSelector> NormalizeBatchSelectors(
        IReadOnlyList<int> pids,
        IReadOnlyList<long?>? processStartUs)
    {
        ArgumentNullException.ThrowIfNull(pids);
        if (pids.Count == 0)
            throw new ArgumentException("pids required and must be non-empty", nameof(pids));
        Validation.RequireCollectionCount(pids.Count);
        if (processStartUs is not null && processStartUs.Count != pids.Count)
        {
            throw new ArgumentException(
                "process_start_selector_count_mismatch: processStartUs must be null or contain exactly one entry per pid.",
                nameof(processStartUs));
        }

        var selectors = new List<BatchSelector>(pids.Count);
        var byPid = new Dictionary<int, long?>();
        for (var index = 0; index < pids.Count; index++)
        {
            var pid = pids[index];
            var processStart = processStartUs?[index];
            Validation.RequirePidTid(pid, tid: null);
            if (processStart is < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(processStartUs),
                    "process_start_selector_invalid: processStartUs entries must be non-negative.");
            }

            if (byPid.TryGetValue(pid, out var prior))
            {
                if (prior != processStart)
                {
                    throw new ArgumentException(
                        $"duplicate_pid_selector_conflict: PID {pid} was supplied with multiple processStartUs selectors.",
                        nameof(pids));
                }
                continue;
            }

            byPid.Add(pid, processStart);
            selectors.Add(new BatchSelector(pid, processStart));
        }

        return selectors;
    }

    internal static IReadOnlyList<BatchScope> ResolveBatchScopes(
        TimeWindow window,
        IReadOnlyList<BatchSelector> selectors,
        IEnumerable<ProcessLifetime> lifetimes) =>
        selectors
            .Select(selector => new BatchScope(
                selector,
                ProcessAnalysisScope.Resolve(
                    window,
                    selector.Pid,
                    selector.ProcessStartUs,
                    lifetimes)))
            .ToArray();

    internal static BatchExecution ExecuteTopFunctionsBatch(
        TraceLog trace,
        int top,
        IReadOnlyList<BatchSelector> selectors,
        TimeWindow window,
        TextWriter symbolLog,
        bool excludeEtwSelfOverhead,
        bool includeTracePct,
        bool resolveSymbols,
        int? timeBudgetMs,
        Func<bool>? stopRequested = null,
        bool? traceHasCpuSamples = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(selectors);
        ArgumentNullException.ThrowIfNull(symbolLog);
        var warnings = new List<string>();
        var identities = TraceIdentityIndex.For(trace);
        var scopes = ResolveBatchScopes(window, selectors, identities.Processes.Lifetimes);
        var resolvedScopes = scopes.Where(item => item.Scope.IsResolved).ToArray();
        foreach (var missing in scopes.Where(item => !item.Scope.IsResolved))
        {
            warnings.Add(
                $"scope_not_found: PID {missing.Selector.Pid} processStartUs=" +
                $"{missing.Selector.ProcessStartUs?.ToString() ?? "<aggregate>"} has no process lifetime in the requested window.");
        }
        if (resolvedScopes.Length == 0)
        {
            return CreateBatchExecution(
                scopes,
                perPid: new Dictionary<int, CpuTopFunctionsResponse>(),
                warnings,
                skippedPids: Array.Empty<int>(),
                traceHasCpuSamples: traceHasCpuSamples);
        }

        var rawByPid = resolvedScopes.ToDictionary(
            item => item.Selector.Pid,
            _ => StackSourceTopN.CreateRawSource(trace, "cpu"));
        var scopeByPid = resolvedScopes.ToDictionary(
            item => item.Selector.Pid,
            item => item.Scope);
        var sampleCountByPid = resolvedScopes.ToDictionary(
            item => item.Selector.Pid,
            _ => 0L);
        var pidsWithSampledProfileStacks = new HashSet<int>();
        var started = Stopwatch.GetTimestamp();
        bool ShouldStop() =>
            stopRequested?.Invoke() == true || BudgetExceeded(started, timeBudgetMs);

        var scanCompleted = !ShouldStop();
        long traceTotalSamples = 0;
        var eventCount = 0;
        if (scanCompleted)
        {
            foreach (var ev in trace.Events)
            {
                if ((++eventCount & 0x3fff) == 0 && ShouldStop())
                {
                    scanCompleted = false;
                    break;
                }

                var usSinceStart = TraceTime.FromMilliseconds(ev.TimeStampRelativeMSec);
                if (!includeTracePct && usSinceStart >= window.EndUs)
                    break;
                if (ev is not SampledProfileTraceData)
                    continue;
                if (includeTracePct)
                    traceTotalSamples++;
                if (!window.ContainsPoint(usSinceStart) ||
                    !scopeByPid.TryGetValue(ev.ProcessID, out var scope) ||
                    !scope.TryResolveEventProcess(
                        identities,
                        ev.ProcessID,
                        usSinceStart,
                        out _))
                {
                    continue;
                }

                rawByPid[ev.ProcessID].AddSample(ev.CallStackIndex(), ev, metric: 1);
                sampleCountByPid[ev.ProcessID]++;
                if (ev.CallStackIndex() != CallStackIndex.Invalid)
                    pidsWithSampledProfileStacks.Add(ev.ProcessID);
            }
        }

        if (!scanCompleted)
        {
            var skipped = resolvedScopes.Select(item => item.Selector.Pid).ToArray();
            if (skipped.Length > 0)
            {
                warnings.Add(TimeBudgetWarning(
                    timeBudgetMs,
                    completed: 0,
                    requested: resolvedScopes.Length,
                    skipped));
            }
            return CreateBatchExecution(
                scopes,
                perPid: new Dictionary<int, CpuTopFunctionsResponse>(),
                warnings,
                skipped,
                traceHasCpuSamples: traceHasCpuSamples);
        }

        var projectionSkipped = new List<int>();
        IReadOnlyDictionary<int, CpuTopFunctionsResponse> perPid;
        using (var symbolReader = StackSourceTopN.OpenSymbolReader(trace, symbolLog))
        {
            perPid = BuildTopFunctionsResponsesForRawSources(
                trace,
                rawByPid,
                symbolReader,
                traceTotalSamples,
                top,
                excludeEtwSelfOverhead,
                hasFilter: true,
                includeTracePct: includeTracePct,
                warnings: warnings,
                resolveSymbols: resolveSymbols,
                shouldStop: ShouldStop,
                skippedPids: projectionSkipped,
                pidsWithSampledProfileStacks: pidsWithSampledProfileStacks,
                processScopes: scopeByPid,
                traceHasCpuSamples: traceHasCpuSamples);
        }

        if (!resolveSymbols && perPid.Count > 0)
        {
            warnings.Add(
                "Symbol resolution skipped for cpu_top_functions_batch fast mode; pass resolveSymbols=true for warmer function names after narrowing the PID set.");
        }
        if (projectionSkipped.Count > 0)
        {
            warnings.Add(TimeBudgetWarning(
                timeBudgetMs,
                perPid.Count,
                resolvedScopes.Length,
                projectionSkipped));
        }

        return CreateBatchExecution(
            scopes,
            perPid,
            warnings,
            projectionSkipped,
            sampleCountByPid,
            traceHasCpuSamples);
    }

    private static BatchExecution CreateBatchExecution(
        IReadOnlyList<BatchScope> scopes,
        IReadOnlyDictionary<int, CpuTopFunctionsResponse> perPid,
        IReadOnlyList<string> warnings,
        IReadOnlyCollection<int> skippedPids,
        IReadOnlyDictionary<int, long>? sampleCountByPid = null,
        bool? traceHasCpuSamples = null)
    {
        var skippedSet = skippedPids.ToHashSet();
        var completedPids = scopes
            .Where(item => perPid.ContainsKey(item.Selector.Pid))
            .Select(item => item.Selector.Pid)
            .ToArray();
        var pidsNotFound = scopes
            .Where(item => !item.Scope.IsResolved)
            .Select(item => item.Selector.Pid)
            .ToArray();
        var pidsWithNoSamples = completedPids
            .Where(pid => sampleCountByPid?.GetValueOrDefault(pid) == 0)
            .ToArray();
        var scopeResults = scopes.Select(item =>
        {
            var pid = item.Selector.Pid;
            var samples = sampleCountByPid?.GetValueOrDefault(pid) ?? 0;
            var sampleContract = ClassifySampleCapability(
                item.Scope.IsResolved,
                samples,
                hasFilter: true,
                traceHasCpuSamples: traceHasCpuSamples,
                scopeNoDataReason: "scope_not_found");
            string resultStatus;
            string? noDataReason;
            if (!item.Scope.IsResolved)
            {
                resultStatus = "scope_not_found";
                noDataReason = "scope_not_found";
            }
            else if (skippedSet.Contains(pid))
            {
                resultStatus = "budget_skipped";
                noDataReason = "budget_exhausted";
            }
            else if (!perPid.ContainsKey(pid))
            {
                resultStatus = "analysis_failed";
                noDataReason = "analysis_failed";
            }
            else if (samples == 0)
            {
                resultStatus = "completed_no_samples";
                noDataReason = sampleContract.NoDataReason;
            }
            else
            {
                resultStatus = "completed";
                noDataReason = null;
            }

            return new CpuBatchScopeResult(
                pid,
                item.Selector.ProcessStartUs,
                resultStatus,
                item.Scope.ScopeStatus,
                item.Scope.ScopeMode,
                item.Scope.SelectedProcess,
                item.Scope.PidReuseObserved,
                item.Scope.IncludedProcesses,
                samples,
                item.Scope.IsResolved &&
                !skippedSet.Contains(pid) &&
                perPid.ContainsKey(pid)
                    ? sampleContract.CapabilityStatus
                    : "unknown",
                noDataReason);
        }).ToArray();
        var hasAnalysisFailure = scopeResults.Any(item => item.ResultStatus == "analysis_failed");

        return new BatchExecution(
            perPid,
            warnings,
            Partial: skippedSet.Count > 0 || hasAnalysisFailure,
            SkippedPids: skippedPids.ToArray(),
            CompletedPids: completedPids,
            PidsNotFound: pidsNotFound,
            PidsWithNoSamples: pidsWithNoSamples,
            ScopeResults: scopeResults);
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
        IReadOnlySet<int>? pidsWithSampledProfileStacks = null,
        IReadOnlyDictionary<int, ProcessAnalysisScope>? processScopes = null,
        bool? traceHasCpuSamples = null)
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
                        pidsWithSampledProfileStacks?.Contains(pid) == true,
                    processScope: processScopes?.GetValueOrDefault(pid),
                    traceHasCpuSamples: traceHasCpuSamples);
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
        bool hasSampledProfileStacks = false,
        ProcessAnalysisScope? processScope = null,
        bool? traceHasCpuSamples = null)
    {
        raw.Source.DoneAddingSamples();
        var lookupAttempt = StackSourceTopN.TryLookupWarmSymbols(
            raw.Source, resolveSymbols, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw, lookupAttempt);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead);
        var stackCoverage = raw.Coverage.Snapshot();
        return BuildTopFunctionsResponse(
            normalized,
            stats,
            traceTotalSamples,
            top,
            hasFilter,
            includeTracePct,
            resolveSymbols,
            hasSampledProfileStacks: hasSampledProfileStacks || stackCoverage.StackedEventCount > 0,
            stackCoverage: stackCoverage,
            processScope: processScope,
            traceHasCpuSamples: traceHasCpuSamples);
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
        bool hasSampledProfileStacks = false,
        DomainStackCoverage? stackCoverage = null,
        ProcessAnalysisScope? processScope = null,
        bool? traceHasCpuSamples = null)
    {
        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = normalized };
        var sourceTotalSamples = filteredSamples ?? (long)callTree.Root.InclusiveCount;
        var totalSamples = (double)Math.Max(1, sourceTotalSamples);
        var hasThreadScopeContract = scope?.IncludedProcesses is not null;
        var scopeResolved = !hasThreadScopeContract || scope!.Value.IsResolved;
        var selectorResolved = scopeResolved && processScope is not { IsResolved: false };
        var sampleContract = ClassifySampleCapability(
            selectorResolved,
            sourceTotalSamples,
            hasFilter,
            traceHasCpuSamples,
            !scopeResolved
                ? scope!.Value.NoDataReason
                : processScope is { IsResolved: false }
                    ? "scope_not_found"
                    : null);

        var rows = callTree.ByID
            .Where(n => n.ExclusiveCount > 0 || n.InclusiveCount > 0)
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
            : stats.ResolutionRate is { } resolutionRate && resolutionRate < 0.8
                ? new List<string> { WarningBuilder.SymbolResolution(resolutionRate) }
                : new List<string>();
        if (hasFilter && !includeTracePct)
        {
            warnings.Add("PctOfTrace omitted; pass includeTracePct=true to compute it (slow on large ETLs).");
        }
        if (processScope is null &&
            scope?.ScopeMode == "pid_aggregate" &&
            scope.Value.PidReuseObserved)
        {
            warnings.Add(
                "ambiguous_process_instance: pid-only scope aggregates multiple process lifetimes.");
        }
        if (processScope is { ScopeMode: "pid_aggregate", PidReuseObserved: true } &&
            scope?.ScopeMode != "single_process")
        {
            warnings.Add(
                "ambiguous_process_instance: pid-only scope aggregates multiple process lifetimes; inspect IncludedProcesses or supply processStartUs.");
        }
        if (stackCoverage is not null)
            StackSourceTopN.AddCoverageWarning(warnings, stackCoverage);
        StackSourceTopN.AddSymbolLookupWarning(warnings, stats);
        if (!string.IsNullOrWhiteSpace(scope?.ScopeWarning))
        {
            warnings.Add(scope.Value.ScopeWarning!);
            if (scope.Value.NoDataReason == ProcessAnalysisScope.NotFoundStatus)
            {
                warnings.Add(
                    "scope_not_found: the requested process/thread selector did not resolve in the requested half-open window.");
            }
        }
        else if (processScope is { IsResolved: false })
        {
            warnings.Add(
                "scope_not_found: the selected PID/processStartUs did not match a process lifetime in the requested half-open window.");
        }
        else if (sourceTotalSamples == 0)
        {
            warnings.Add(sampleContract.NoDataReason == "event_class_not_observed"
                ? "event_class_not_observed: no sampled CPU events were observed anywhere in the materialized trace; this does not prove CPU sampling was disabled."
                : "no_events_in_scope: no sampled CPU events matched the selected process scope and half-open window; capture capability remains unknown.");
        }

        var selectedProcess = hasThreadScopeContract
            ? scope?.Process?.Key ?? scope?.Thread?.Key.Process
            : processScope?.SelectedProcess ?? scope?.Process?.Key;
        var scopeMode = hasThreadScopeContract
            ? scope!.Value.ScopeMode
            : processScope?.ScopeMode ?? scope switch
        {
            { Process: not null } => "single_process",
            { Pid: not null } => "pid_aggregate",
            _ => "all_processes",
        };
        IReadOnlyList<ProcessInstanceKey>? includedProcesses =
            hasThreadScopeContract
                ? scope!.Value.IncludedProcesses
                : processScope?.IncludedProcesses ??
            (scope?.Process is { } selectedLifetime
                ? [selectedLifetime.Key]
                : null);
        return new CpuTopFunctionsResponse(
            rows,
            stats,
            warnings,
            TotalSamples: sourceTotalSamples,
            SelectedProcess: selectedProcess,
            SelectedThread: scope?.Thread?.Key,
            HasSampledProfileStacks: hasSampledProfileStacks,
            SymbolResolutionState: StackSourceTopN.GetSymbolResolutionState(
                resolveSymbols, stats, hasSampledProfileStacks),
            StackCoverage: stackCoverage,
            ScopeMode: scopeMode,
            PidReuseObserved: processScope?.PidReuseObserved ?? scope?.PidReuseObserved == true,
            IncludedProcesses: includedProcesses,
            ScopeStatus: hasThreadScopeContract
                ? scope!.Value.ScopeStatus
                : processScope?.ScopeStatus ?? ProcessAnalysisScope.ResolvedStatus,
            NoDataReason: sampleContract.NoDataReason,
            CapabilityStatus: sampleContract.CapabilityStatus,
            MatchedEventCount: scopeResolved
                ? stackCoverage?.TotalEventCount ?? sourceTotalSamples
                : 0,
            IncludedThreads: hasThreadScopeContract
                ? scope!.Value.IncludedThreads
                : scope?.Thread is { } thread
                    ? [new ThreadScopeCandidate(thread.Key, thread.StartUs, thread.EndUs)]
                    : []);
    }

    private static (string CapabilityStatus, string? NoDataReason) ClassifySampleCapability(
        bool scopeResolved,
        long scopedSampleCount,
        bool hasFilter,
        bool? traceHasCpuSamples,
        string? scopeNoDataReason)
    {
        if (!scopeResolved)
            return ("unknown", scopeNoDataReason ?? "scope_not_found");
        if (scopedSampleCount > 0)
            return ("observed", null);

        // A filtered scan cannot infer trace-wide absence from its own empty result.
        // MCP entry points pass the already-cached trace capability; legacy analyzer
        // callers retain the old unfiltered inference when no explicit value is supplied.
        var traceEventClassAbsent = traceHasCpuSamples == false ||
                                    (traceHasCpuSamples is null && !hasFilter);
        return traceEventClassAbsent
            ? ("not_observed", "event_class_not_observed")
            : ("unknown", "no_events_in_scope");
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
        bool resolveSymbols = false,
        bool? traceHasCpuSamples = null)
    {
        var scope = ResolveLegacyScope(trace, pid, startUs, endUs);
        var processScope = ProcessAnalysisScope.Resolve(
            scope.Window,
            pid,
            processStartUs: null,
            TraceIdentityIndex.For(trace));
        return CallerCallee(
            trace,
            focusFunction,
            top,
            scope,
            symbolLog,
            excludeEtwSelfOverhead,
            resolveSymbols,
            processScope,
            traceHasCpuSamples);
    }

    internal static CallerCalleeResponse CallerCallee(
        TraceLog trace,
        string focusFunction,
        int top,
        ThreadAnalysisScope scope,
        TextWriter symbolLog,
        bool excludeEtwSelfOverhead = false,
        bool resolveSymbols = false,
        ProcessAnalysisScope? processScope = null,
        bool? traceHasCpuSamples = null)
    {
        var (normalized, stats, _, filteredSamples, hasSampledProfileStacks, stackCoverage) = BuildNormalized(
            trace,
            scope,
            symbolLog,
            excludeEtwSelfOverhead,
            countTraceTotalSamples: false,
            resolveSymbols,
            processScope);
        var baseWarnings = !resolveSymbols
            ? new List<string> { WarningBuilder.SymbolResolutionSkipped("cpu_caller_callee") }
            : stats.ResolutionRate is { } resolutionRate && resolutionRate < 0.8
                ? new List<string> { WarningBuilder.SymbolResolution(resolutionRate) }
                : new List<string>();
        StackSourceTopN.AddSymbolLookupWarning(baseWarnings, stats);
        if (scope.ScopeMode == "pid_aggregate" && scope.PidReuseObserved)
        {
            baseWarnings.Add(
                "ambiguous_process_instance: pid-only scope aggregates multiple process lifetimes.");
        }

        var hasFilter = scope.Pid.HasValue || scope.Thread is not null ||
                        processScope?.ProcessStartUs.HasValue == true;
        var contract = scope.IncludedProcesses is not null
            ? StackResultContract.FromThreadScope(scope, hasFilter, stackCoverage)
            : processScope is not null
                ? StackResultContract.From(processScope, hasFilter, stackCoverage)
                : StackResultContract.FromThreadScope(scope, hasFilter, stackCoverage);
        var sampleContract = ClassifySampleCapability(
            contract.ScopeStatus == ProcessAnalysisScope.ResolvedStatus,
            filteredSamples,
            hasFilter,
            traceHasCpuSamples,
            contract.NoDataReason);
        contract = contract with
        {
            CapabilityStatus = sampleContract.CapabilityStatus,
            NoDataReason = filteredSamples == 0
                ? sampleContract.NoDataReason
                : contract.NoDataReason,
        };

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
                resolveSymbols, stats, hasSampledProfileStacks),
            stackCoverage: stackCoverage,
            resultContract: contract);
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
        bool HasSampledProfileStacks,
        DomainStackCoverage StackCoverage)
        BuildNormalized(
            TraceLog trace,
            ThreadAnalysisScope scope,
            TextWriter symbolLog,
            bool excludeEtwSelfOverhead,
            bool countTraceTotalSamples,
            bool resolveSymbols,
            ProcessAnalysisScope? processScope = null)
    {
        using var symbolReader = StackSourceTopN.OpenSymbolReader(trace, symbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace, "cpu");
        var identities = processScope is null ? null : TraceIdentityIndex.For(trace);
        long traceTotalSamples = 0;
        long filteredSamples = 0;
        var hasSampledProfileStacks = false;
        foreach (var ev in trace.Events)
        {
            var usSinceStart = TraceTime.FromMilliseconds(ev.TimeStampRelativeMSec);
            if (!countTraceTotalSamples && usSinceStart >= scope.Window.EndUs) break;

            if (ev is not SampledProfileTraceData) continue;
            if (countTraceTotalSamples) traceTotalSamples++;
            if (!PassesScope(scope, ev.ProcessID, ev.ThreadID, usSinceStart) ||
                processScope is not null &&
                !processScope.MatchesEvent(
                    identities!,
                    ev.ProcessID,
                    usSinceStart))
            {
                continue;
            }

            filteredSamples++;
            if (ev.CallStackIndex() != CallStackIndex.Invalid)
                hasSampledProfileStacks = true;
            raw.AddSample(ev.CallStackIndex(), ev, metric: 1);
        }
        raw.Source.DoneAddingSamples();

        var lookupAttempt = StackSourceTopN.TryLookupWarmSymbols(
            raw.Source, resolveSymbols, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw, lookupAttempt);
        var normalized = StackSourceTopN.BuildNormalized(raw.Source, trace, excludeEtwSelfOverhead);
        var stackCoverage = raw.Coverage.Snapshot();
        return (
            normalized,
            stats,
            traceTotalSamples,
            filteredSamples,
            hasSampledProfileStacks,
            stackCoverage);
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
