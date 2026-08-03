using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class ThreadComparisonTools
{
    private const int MaxWindows = 32;
    private const int MaxEvidenceRowsPerDomain = 50;
    private static readonly IReadOnlyList<string> EvidenceBoundaries =
    [
        "SAMPLED_CPU_NOT_EXACT_TIME",
        "READY_LATENCY_REQUIRES_READY_THREAD",
        "SCOPED_STACK_COVERAGE_REQUIRED",
        "THREAD_INSTANCE_SCOPE_REQUIRED",
        "COMPOSITE_PARTIAL_EVIDENCE",
        "ASSOCIATION_NOT_CAUSATION",
        "TOP_N_NOT_COMPLETE",
    ];

    private readonly TraceCache _cache;
    private readonly CpuTools _cpu;
    private readonly WaitTools _wait;
    private readonly CapabilityDiscoveryRuntime? _capabilityDiscovery;

    public ThreadComparisonTools(
        TraceCache cache,
        IPrivacyLogSink? privacyLog = null,
        CapabilityDiscoveryRuntime? capabilityDiscovery = null)
    {
        _cache = cache;
        _cpu = new CpuTools(cache, privacyLog, capabilityDiscovery);
        _wait = new WaitTools(cache, privacyLog);
        _capabilityDiscovery = capabilityDiscovery;
    }

    [McpServerTool(
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false), Description(
        "Compare one exact PID/TID instance across two or more named half-open time windows. " +
        "The tool resolves the thread once across the union of all windows, then pins that " +
        "process/thread generation for every sub-analysis so PID/TID reuse is never silently mixed. " +
        "Each window reports sampled CPU counts separately from scheduler-derived running time, " +
        "ready-to-run latency, and off-CPU blocked duration, plus bounded CPU and switch-out stack " +
        "evidence. Ready latency can lie within an off-CPU interval and must not be added to blocked " +
        "duration. Wait reasons and stacks are associations, not proof of a blocking method or root " +
        "cause. Rows are cursor-paged from one immutable snapshot; missing stacks or symbols remain " +
        "explicit coverage gaps rather than inferred method attribution.")]
    public ThreadCompareWindowsResponse ThreadCompareWindows(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Process ID containing the thread")] int pid,
        [Description("Thread ID to compare; resolved to one exact lifetime across the union of all windows")] int tid,
        [Description("Two to 32 uniquely named half-open windows in request order")]
        ThreadComparisonWindowInput[] windows,
        [Description("Top CPU and wait stack rows retained inside each atomic window row (default 12, max 50)")]
        int top = 12,
        [Description("Fold known ETW stack-walk overhead into [ETW Overhead]")]
        bool excludeEtwSelfOverhead = false,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false,
        [Description("Optional exact process start in trace-relative microseconds")]
        long? processStartUs = null,
        [Description("Optional exact thread start in trace-relative microseconds")]
        long? threadStartUs = null,
        [Description("Optional exact thread generation returned by CPU/Wait thread rows")]
        long? threadGeneration = null,
        [Description("Maximum complete window rows requested per page (default 2, max 32)")]
        int pageSize = 2,
        [Description("Opaque qrc_ continuation bound to the trace generation, query, and immutable comparison snapshot")]
        string? cursor = null)
    {
        var normalized = NormalizeWindows(windows);
        Validation.RequireThreadSelector(
            pid, tid, processStartUs, threadStartUs, threadGeneration);
        if (top is < 1 or > MaxEvidenceRowsPerDomain)
            throw new ArgumentOutOfRangeException(nameof(top), $"top must be between 1 and {MaxEvidenceRowsPerDomain}.");
        if (pageSize is < 1 or > MaxWindows)
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"pageSize must be between 1 and {MaxWindows}.");

        using var traceLease = _cache.Acquire(traceId);
        var query = TimelinePagination.CanonicalQuery(
            TimelinePagination.ThreadCompareWindowsTool,
            ("pid", TimelinePagination.Number(pid)),
            ("tid", TimelinePagination.Number(tid)),
            ("windows", JsonSerializer.Serialize(normalized, McpJsonUtilities.DefaultOptions)),
            ("top", TimelinePagination.Number(top)),
            ("excludeEtwSelfOverhead", excludeEtwSelfOverhead ? "true" : "false"),
            ("resolveSymbols", resolveSymbols ? "true" : "false"),
            ("processStartUs", TimelinePagination.OptionalNumber(processStartUs)),
            ("threadStartUs", TimelinePagination.OptionalNumber(threadStartUs)),
            ("threadGeneration", TimelinePagination.OptionalNumber(threadGeneration)),
            ("pageSize", TimelinePagination.Number(pageSize)));
        var context = TimelinePagination.CreateContext(
            traceLease,
            traceId,
            TimelinePagination.ThreadCompareWindowsTool,
            query,
            TimelinePagination.ThreadCompareWindowsOrdering);
        var pagination = _capabilityDiscovery is null
            ? null
            : ThreadComparisonPaginationRuntime.For(_capabilityDiscovery);
        if (cursor is not null)
        {
            if (pagination is null)
            {
                throw new QueryResultCursorException(
                    QueryResultCursorFailureKind.Invalid,
                    "Thread comparison continuation requires the production pagination runtime.");
            }

            return pagination.Resume(context, cursor, pageSize);
        }

        var traceEndUs = TraceTime.FromMilliseconds(
            traceLease.Trace.SessionDuration.TotalMilliseconds);
        var resolvedWindows = normalized.Select(window =>
        {
            var resolved = Validation.RequireWindowInput(window.StartUs, window.EndUs)
                .Resolve(traceEndUs, maxDurationUs: null);
            return window with { StartUs = resolved.StartUs, EndUs = resolved.EndUs };
        }).ToArray();
        var union = new TimeWindow(
            resolvedWindows.Min(window => window.StartUs),
            resolvedWindows.Max(window => window.EndUs));
        var scope = WaitTools.ResolveStackScope(
            union,
            pid,
            tid,
            processStartUs,
            threadStartUs,
            TraceIdentityIndex.For(traceLease.Trace),
            threadGeneration);

        var complete = scope.IsResolved && scope.Thread is not null
            ? AnalyzeWindows(
                traceId,
                resolvedWindows,
                scope,
                top,
                excludeEtwSelfOverhead,
                resolveSymbols)
            : Unresolved(scope);
        return pagination is null
            ? complete
            : pagination.Start(context, complete, pageSize);
    }

    internal static IReadOnlyList<ThreadComparisonWindowInput> NormalizeWindows(
        ThreadComparisonWindowInput[] windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Length is < 2 or > MaxWindows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windows),
                $"windows must contain between 2 and {MaxWindows} entries.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new ThreadComparisonWindowInput[windows.Length];
        for (var index = 0; index < windows.Length; index++)
        {
            var window = windows[index]
                ?? throw new ArgumentException("windows cannot contain null entries.", nameof(windows));
            var name = window.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
                throw new ArgumentException("Each window name must contain 1 to 80 non-whitespace characters.", nameof(windows));
            if (!names.Add(name))
                throw new ArgumentException($"Window name '{name}' is duplicated.", nameof(windows));
            if (window.StartUs < 0 || window.EndUs <= window.StartUs)
                throw new ArgumentException($"Window '{name}' must satisfy 0 <= startUs < endUs.", nameof(windows));
            normalized[index] = window with { Name = name };
        }

        return normalized;
    }

    private ThreadCompareWindowsResponse AnalyzeWindows(
        string path,
        IReadOnlyList<ThreadComparisonWindowInput> windows,
        ThreadAnalysisScope scope,
        int top,
        bool excludeEtwSelfOverhead,
        bool resolveSymbols)
    {
        var thread = scope.Thread!;
        var rows = new List<ThreadComparisonWindowRow>(windows.Count);
        foreach (var window in windows)
        {
            var cpu = _cpu.CpuTopFunctions(
                path,
                top: top,
                pid: thread.Key.Process.Pid,
                startUs: window.StartUs,
                endUs: window.EndUs,
                excludeEtwSelfOverhead: excludeEtwSelfOverhead,
                includeTracePct: false,
                resolveSymbols: resolveSymbols,
                tid: thread.Key.Tid,
                processStartUs: thread.Key.Process.StartUs,
                threadStartUs: thread.StartUs,
                threadGeneration: thread.Key.Generation);
            var precise = _cpu.CpuPreciseAnalysis(
                path,
                top: 1,
                pid: thread.Key.Process.Pid,
                startUs: window.StartUs,
                endUs: window.EndUs,
                tid: thread.Key.Tid,
                processStartUs: thread.Key.Process.StartUs,
                threadStartUs: thread.StartUs,
                threadGeneration: thread.Key.Generation);
            var wait = _wait.WaitAnalysis(
                path,
                top: 1,
                pid: thread.Key.Process.Pid,
                startUs: window.StartUs,
                endUs: window.EndUs,
                tid: thread.Key.Tid,
                processStartUs: thread.Key.Process.StartUs,
                threadStartUs: thread.StartUs,
                threadGeneration: thread.Key.Generation);
            var waitStacks = _wait.WaitTopStacks(
                path,
                top: top,
                pid: thread.Key.Process.Pid,
                startUs: window.StartUs,
                endUs: window.EndUs,
                resolveSymbols: resolveSymbols,
                tid: thread.Key.Tid,
                processStartUs: thread.Key.Process.StartUs,
                threadStartUs: thread.StartUs,
                threadGeneration: thread.Key.Generation);
            IReadOnlyList<WaitReasonBucket> waitReasons =
                wait.Rows.FirstOrDefault()?.TopWaitReasons ?? [];
            var warnings = cpu.Warnings
                .Concat(precise.Warnings)
                .Concat(wait.Warnings)
                .Concat(waitStacks.Warnings)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            rows.Add(new ThreadComparisonWindowRow(
                Name: window.Name,
                StartUs: window.StartUs,
                EndUs: window.EndUs,
                WindowDurationUs: checked(window.EndUs - window.StartUs),
                SampledCpuSamples: cpu.TotalSamples,
                RunningUs: precise.TotalCpuUs,
                ContextSwitches: precise.TotalContextSwitches,
                ReadyCount: precise.TotalReadyCount,
                ReadyLatencyUs: precise.TotalReadyLatencyUs,
                BlockedUs: wait.TotalBlockedUs,
                BlockedSwitchOutCount: wait.ScopedCSwitches,
                BlockedIntervalCount: wait.MatchedIntervalCount,
                TopCpuFunctions: cpu.Rows,
                TopWaitReasons: waitReasons,
                TopWaitFunctions: waitStacks.Rows,
                CpuStackCoverage: cpu.StackCoverage,
                WaitStackCoverage: waitStacks.StackCoverage,
                CpuCapabilityStatus: cpu.CapabilityStatus,
                SchedulerCapabilityStatus: precise.CapabilityStatus,
                WaitCapabilityStatus: wait.CapabilityStatus,
                WaitStackCapabilityStatus: waitStacks.CapabilityStatus,
                CpuSymbolResolutionState: cpu.SymbolResolutionState,
                WaitSymbolResolutionState: waitStacks.SymbolResolutionState,
                CpuNoDataReason: cpu.NoDataReason,
                SchedulerNoDataReason: precise.NoDataReason,
                WaitNoDataReason: wait.NoDataReason,
                WaitStackNoDataReason: waitStacks.NoDataReason,
                Warnings: warnings));
        }

        var matchedEventCount = rows.Aggregate(
            0L,
            static (total, row) => checked(total + row.ContextSwitches));
        var hasEvidence = rows.Any(row =>
            row.SampledCpuSamples > 0 || row.ContextSwitches > 0 ||
            row.BlockedIntervalCount > 0);
        return new ThreadCompareWindowsResponse(
            Rows: rows,
            Warnings:
            [
                "sampled_cpu_is_count_not_time: SampledCpuSamples is a sample count; use RunningUs for scheduler-derived on-CPU duration.",
                "ready_blocked_not_additive: ReadyLatencyUs may occur within an off-CPU blocked interval and must not be added to BlockedUs.",
                "stack_association_not_root_cause: wait reasons and switch-out stacks do not prove the responsible method or component.",
            ],
            SelectedProcess: thread.Key.Process,
            SelectedThread: thread.Key,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: scope.IncludedProcesses ?? [thread.Key.Process],
            IncludedThreads: scope.IncludedThreads ??
                [new ThreadScopeCandidate(thread.Key, thread.StartUs, thread.EndUs)],
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: hasEvidence ? "observed" : "unknown",
            MatchedEventCount: matchedEventCount,
            NoDataReason: hasEvidence ? null : "no_events_in_scope",
            DoesNotProve: EvidenceBoundaries,
            BaselineWindowName: windows[0].Name,
            TotalWindowCount: rows.Count,
            ReturnedCount: rows.Count);
    }

    private static ThreadCompareWindowsResponse Unresolved(ThreadAnalysisScope scope)
    {
        var warnings = string.IsNullOrWhiteSpace(scope.ScopeWarning)
            ? Array.Empty<string>()
            : new[] { scope.ScopeWarning };
        return new ThreadCompareWindowsResponse(
            Rows: [],
            Warnings: warnings,
            SelectedProcess: scope.Process?.Key ?? scope.Thread?.Key.Process,
            SelectedThread: scope.Thread?.Key,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: scope.IncludedProcesses ?? [],
            IncludedThreads: scope.IncludedThreads ?? [],
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: "unknown",
            MatchedEventCount: 0,
            NoDataReason: scope.NoDataReason ?? scope.ScopeStatus,
            DoesNotProve: EvidenceBoundaries,
            BaselineWindowName: null,
            TotalWindowCount: 0,
            ReturnedCount: 0);
    }
}
