using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

internal sealed record WaitAnalysisDetailedResult(
    WaitAnalysisResponse Response,
    IReadOnlyList<WaitAnalysisRow> CompleteRows);

public static class WaitAnalysis
{
    private static readonly string[] WaitReasonNames =
    {
        /* 0  */ "Executive",
        /* 1  */ "FreePage",
        /* 2  */ "PageIn",
        /* 3  */ "PoolAllocation",
        /* 4  */ "DelayExecution",
        /* 5  */ "Suspended",
        /* 6  */ "UserRequest",
        /* 7  */ "WrExecutive",
        /* 8  */ "WrFreePage",
        /* 9  */ "WrPageIn",
        /* 10 */ "WrPoolAllocation",
        /* 11 */ "WrDelayExecution",
        /* 12 */ "WrSuspended",
        /* 13 */ "WrUserRequest",
        /* 14 */ "WrSpare0",
        /* 15 */ "WrQueue",
        /* 16 */ "WrLpcReceive",
        /* 17 */ "WrLpcReply",
        /* 18 */ "WrVirtualMemory",
        /* 19 */ "WrPageOut",
        /* 20 */ "WrRendezvous",
        /* 21 */ "WrKeyedEvent",
        /* 22 */ "WrTerminated",
        /* 23 */ "WrProcessInSwap",
        /* 24 */ "WrCpuRateControl",
        /* 25 */ "WrCalloutStack",
        /* 26 */ "WrKernel",
        /* 27 */ "WrResource",
        /* 28 */ "WrPushLock",
        /* 29 */ "WrMutex",
        /* 30 */ "WrQuantumEnd",
        /* 31 */ "WrDispatchInt",
        /* 32 */ "WrPreempted",
        /* 33 */ "WrYieldExecution",
        /* 34 */ "WrFastMutex",
        /* 35 */ "WrGuardedMutex",
        /* 36 */ "WrRundown",
        /* 37 */ "WrAlertByThreadId",
        /* 38 */ "WrDeferredPreempt",
        /* 39 */ "WrPhysicalFault",
        /* 40 */ "WrIoRing",
        /* 41 */ "WrMdlCache",
    };

    public static string WaitReasonName(ThreadWaitReason reason)
    {
        var index = (int)reason;
        return (uint)index < (uint)WaitReasonNames.Length
            ? WaitReasonNames[index]
            : $"Wait_{index}";
    }

    public static WaitAnalysisResponse Analyze(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs)
    {
        RequirePositiveTop(top);
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds),
            maxDurationUs: null);
        var identities = TraceIdentityIndex.For(trace);
        var processScope = ProcessAnalysisScope.Resolve(
            window, pid, processStartUs: null, identities);
        if (!processScope.IsResolved)
            return EmptyResolutionFailure("process_scope_unresolved", processScope);

        var resolution = ThreadAnalysisScope.Resolve(
            window, pid, tid: null, processStartUs: null, threadStartUs: null,
            identities);
        if (resolution.Status != InstanceResolutionStatus.Resolved ||
            !resolution.Value.HasValue)
        {
            return EmptyResolutionFailure(
                $"thread_scope_{resolution.Status.ToString().ToLowerInvariant()}",
                processScope);
        }

        return Analyze(trace, top, resolution.Value.Value, processScope);
    }

    internal static WaitAnalysisResponse Analyze(
        TraceLog trace,
        int top,
        ThreadAnalysisScope scope,
        ProcessAnalysisScope? processScope = null) =>
        AnalyzeDetailed(trace, top, scope, processScope).Response;

    internal static WaitAnalysisDetailedResult AnalyzeDetailed(
        TraceLog trace,
        int top,
        ThreadAnalysisScope scope,
        ProcessAnalysisScope? processScope = null)
    {
        RequirePositiveTop(top);
        var identities = TraceIdentityIndex.For(trace);
        var projection = new WaitProjectionAccumulator(
            scope,
            processScope,
            identities.Threads.StartUsFor,
            identities);
        var stream = SchedulerIntervalTraceReader.Read(trace, identities, [projection]);
        var warnings = BuildSchedulerWarnings(
            stream.Completion, stream.IdentityDiagnosticCount);
        return projection.BuildDetailed(
            top,
            stream.Completion.UnmatchedBlockedIntervalCount,
            warnings,
            stream.Completion.CountScopedUnmatchedBlockedIntervals(scope));
    }

    internal static WaitAnalysisResponse Project(
        IEnumerable<BlockedInterval> intervals,
        ThreadAnalysisScope scope,
        int top,
        IEnumerable<RunningInterval>? runningIntervals = null,
        int unmatchedBlockedIntervalCount = 0,
        IReadOnlyDictionary<ThreadInstanceKey, string>? processNames = null,
        IReadOnlyDictionary<ThreadInstanceKey, long>? contextSwitches = null,
        long totalCSwitches = 0,
        long? traceCSwitchCount = null,
        IEnumerable<string>? warnings = null,
        bool hasContextSwitchBlockingStacks = false,
        long? scopedCSwitches = null,
        long? scopedStackedSwitches = null,
        ProcessAnalysisScope? processScope = null,
        Func<ThreadInstanceKey, long?>? threadStartUs = null,
        int? scopedUnmatchedBlockedIntervalCount = null,
        long traceIdentityUnresolvedCSwitchSideCount = 0,
        long scopedIdentityUnresolvedCSwitchSideCount = 0)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        RequirePositiveTop(top);
        if (unmatchedBlockedIntervalCount < 0)
            throw new ArgumentOutOfRangeException(nameof(unmatchedBlockedIntervalCount));
        if (totalCSwitches < 0)
            throw new ArgumentOutOfRangeException(nameof(totalCSwitches));
        if (traceCSwitchCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(traceCSwitchCount));
        if (scopedCSwitches is < 0)
            throw new ArgumentOutOfRangeException(nameof(scopedCSwitches));
        if (scopedStackedSwitches is < 0)
            throw new ArgumentOutOfRangeException(nameof(scopedStackedSwitches));
        if (traceIdentityUnresolvedCSwitchSideCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(traceIdentityUnresolvedCSwitchSideCount));
        }
        if (scopedIdentityUnresolvedCSwitchSideCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scopedIdentityUnresolvedCSwitchSideCount));
        }

        var projection = new WaitProjectionAccumulator(
            scope, processScope, threadStartUs);
        projection.AddProcessNames(processNames);
        foreach (var interval in AnalysisEvents.Enumerate(intervals))
            projection.OnBlocked(interval);
        foreach (var interval in AnalysisEvents.Enumerate(
                     runningIntervals ?? Array.Empty<RunningInterval>()))
            projection.OnRunning(interval);
        projection.AddContextSwitches(contextSwitches);
        projection.SetEventSummary(
            totalCSwitches,
            traceCSwitchCount,
            hasContextSwitchBlockingStacks,
            scopedCSwitches,
            scopedStackedSwitches,
            traceIdentityUnresolvedCSwitchSideCount,
            scopedIdentityUnresolvedCSwitchSideCount);
        return projection.Build(
            top,
            unmatchedBlockedIntervalCount,
            warnings,
            scopedUnmatchedBlockedIntervalCount ?? unmatchedBlockedIntervalCount);
    }

    internal static WaitAnalysisResponse EmptyResolutionFailure(
        string warningCode,
        ProcessAnalysisScope processScope)
    {
        var failureStatus = processScope.IsResolved
            ? ProcessAnalysisScope.NotFoundStatus
            : processScope.ScopeStatus;
        var failureWarning = processScope.IsResolved
            ? $"scope_not_found: {warningCode}; the requested process/thread selector did not resolve to one analyzable scope in the requested half-open window."
            : ProcessAnalysisScope.ResolutionFailureWarning(failureStatus);
        return new(
            Rows: Array.Empty<WaitAnalysisRow>(),
            TotalCSwitches: 0,
            Warnings:
            [
                failureWarning,
            ],
            TotalBlockedUs: 0,
            UnmatchedBlockedIntervalCount: 0,
            SelectedProcess: processScope.SelectedProcess,
            SelectedThread: null,
            ScopeMode: processScope.ScopeMode,
            PidReuseObserved: processScope.PidReuseObserved,
            IncludedProcesses: processScope.IncludedProcesses,
            ScopeStatus: failureStatus,
            CapabilityStatus: "unknown",
            MatchedEventCount: 0,
            NoDataReason: failureStatus);
    }

    internal static WaitAnalysisResponse EmptyResolutionFailure(
        ThreadAnalysisScope scope)
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(scope.ScopeWarning))
            warnings.Add(scope.ScopeWarning);
        if (scope.NoDataReason == ProcessAnalysisScope.NotFoundStatus)
        {
            warnings.Add(
                "scope_not_found: the requested process/thread selector did not resolve in the requested half-open window.");
        }

        return new WaitAnalysisResponse(
            Rows: [],
            TotalCSwitches: 0,
            Warnings: warnings,
            TotalBlockedUs: 0,
            UnmatchedBlockedIntervalCount: 0,
            SelectedProcess: scope.Process?.Key ?? scope.Thread?.Key.Process,
            SelectedThread: null,
            HasContextSwitches: false,
            HasContextSwitchBlockingStacks: false,
            SymbolResolutionState: "not_applicable",
            WindowCSwitchesAllThreads: 0,
            ScopedCSwitches: 0,
            ScopedStackedSwitches: 0,
            ScopedStackCoveragePct: null,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: scope.IncludedProcesses ?? [],
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: "unknown",
            MatchedEventCount: 0,
            NoDataReason: scope.NoDataReason,
            IncludedThreads: scope.IncludedThreads ?? []);
    }

    private static void RequirePositiveTop(int top)
    {
        if (top <= 0)
            throw new ArgumentOutOfRangeException(nameof(top));
    }

    internal static IReadOnlyList<string> BuildSchedulerWarnings(
        SchedulerIntervalResult completion,
        long unresolvedIdentityCount)
    {
        var warnings = new List<string>();
        if (unresolvedIdentityCount > 0)
        {
            warnings.Add(
                $"identity_unresolved: scheduler_identity_unresolved; {unresolvedIdentityCount:N0} event-side identity resolution(s) were unavailable or ambiguous.");
        }
        if (completion.IdentityMismatchCount > 0)
        {
            warnings.Add(
                $"scheduler_identity_mismatch: {completion.IdentityMismatchCount:N0} scheduler state transition(s) did not match the resolved thread instance.");
        }
        if (completion.UnmatchedRunningIntervalCount > 0)
        {
            warnings.Add(
                $"unmatched_running_interval: {completion.UnmatchedRunningIntervalCount:N0} running interval(s) were dropped rather than guessed.");
        }
        return warnings;
    }

    private static ThreadProjection GetProjection(
        Dictionary<ThreadInstanceKey, ThreadProjection> projections,
        ThreadInstanceKey thread,
        IReadOnlyDictionary<ThreadInstanceKey, string>? processNames)
    {
        if (!projections.TryGetValue(thread, out var projection))
        {
            projection = new ThreadProjection(thread, threadStartUs: 0);
            projections.Add(thread, projection);
        }

        if (string.IsNullOrEmpty(projection.ProcessName) &&
            processNames is not null &&
            processNames.TryGetValue(thread, out var processName) &&
            !string.IsNullOrEmpty(processName))
        {
            projection.ProcessName = processName;
        }
        return projection;
    }

    private static WaitAnalysisRow ToRow(ThreadProjection projection)
    {
        var reasons = projection.WaitReasons
            .OrderByDescending(item => item.Value.BlockedUs)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new WaitReasonBucket(
                item.Key, item.Value.BlockedUs, item.Value.Count))
            .ToList();
        return new WaitAnalysisRow(
            projection.Key.Process.Pid,
            projection.ProcessName,
            projection.Key.Tid,
            projection.CpuUs,
            projection.BlockedUs,
            projection.CpuUs > 0
                ? (double)projection.BlockedUs / projection.CpuUs
                : null,
            projection.ContextSwitches,
            reasons,
            ProcessStartUs: projection.Key.Process.StartUs,
            ThreadGeneration: projection.Key.Generation,
            ThreadStartUs: projection.ThreadStartUs);
    }

    internal sealed class WaitProjectionAccumulator :
        ISchedulerIntervalSink,
        ISchedulerEventSink
    {
        private readonly ThreadAnalysisScope _scope;
        private readonly ProcessAnalysisScope? _processScope;
        private readonly Func<ThreadInstanceKey, long?>? _threadStartUs;
        private readonly TraceIdentityIndex? _identities;
        private readonly Dictionary<ThreadInstanceKey, ThreadProjection> _projections = new();
        private readonly Dictionary<ThreadInstanceKey, string> _processNames = new();
        private long _totalBlockedUs;
        private long _totalCpuUs;
        private long _windowCSwitchesAllThreads;
        private long _scopedCSwitches;
        private long _scopedStackedSwitches;
        private long _matchedBlockedIntervalCount;
        private long _traceIdentityUnresolvedCSwitchSideCount;
        private long _scopedIdentityUnresolvedCSwitchSideCount;
        private long? _traceCSwitchCount = 0;

        public WaitProjectionAccumulator(
            ThreadAnalysisScope scope,
            ProcessAnalysisScope? processScope = null,
            Func<ThreadInstanceKey, long?>? threadStartUs = null,
            TraceIdentityIndex? identities = null)
        {
            _scope = scope;
            _processScope = processScope;
            _threadStartUs = threadStartUs;
            _identities = identities;
        }

        public void OnRunning(in RunningInterval interval)
        {
            var accountedUs = _scope.AccountInterval(
                interval.Thread, interval.StartUs, interval.EndUs);
            if (accountedUs <= 0)
                return;

            _totalCpuUs = checked(_totalCpuUs + accountedUs);
            var projection = GetScopedProjection(interval.Thread);
            projection.CpuUs = checked(projection.CpuUs + accountedUs);
        }

        public void OnBlocked(in BlockedInterval interval)
        {
            var accountedUs = _scope.AccountInterval(
                interval.Thread, interval.StartUs, interval.EndUs);
            if (accountedUs <= 0)
                return;

            _totalBlockedUs = checked(_totalBlockedUs + accountedUs);
            _matchedBlockedIntervalCount = checked(
                _matchedBlockedIntervalCount + 1);
            var projection = GetScopedProjection(interval.Thread);
            projection.BlockedUs = checked(projection.BlockedUs + accountedUs);
            var previous = projection.WaitReasons.GetValueOrDefault(interval.WaitReason);
            projection.WaitReasons[interval.WaitReason] = (
                checked(previous.BlockedUs + accountedUs),
                checked(previous.Count + 1));
        }

        public void OnContextSwitch(in SchedulerSwitchObservation observation)
        {
            _traceCSwitchCount = checked(_traceCSwitchCount.GetValueOrDefault() + 1);
            ObserveUnresolvedSide(
                observation.OldIdentityUnresolved,
                observation.OldPid,
                observation.OldTid,
                observation.TimestampUs);
            ObserveUnresolvedSide(
                observation.NewIdentityUnresolved,
                observation.NewPid,
                observation.NewTid,
                observation.TimestampUs);
            if (_scope.Window.ContainsPoint(observation.TimestampUs))
            {
                _windowCSwitchesAllThreads = checked(_windowCSwitchesAllThreads + 1);
                if (observation.OldThread.HasValue &&
                    _scope.MatchesPoint(
                        observation.OldThread.Value,
                        observation.TimestampUs))
                {
                    _scopedCSwitches = checked(_scopedCSwitches + 1);
                    if (observation.BlockingStack != CallStackIndex.Invalid)
                    {
                        _scopedStackedSwitches = checked(
                            _scopedStackedSwitches + 1);
                    }
                }
            }

            if (observation.OldThread.HasValue)
            {
                ObserveSwitchOut(
                    observation.OldThread.Value,
                    observation.OldProcessName,
                    observation.TimestampUs);
            }
            if (observation.NewThread.HasValue)
            {
                RememberProcessName(
                    observation.NewThread.Value,
                    observation.NewProcessName);
            }
        }

        public void AddProcessNames(
            IReadOnlyDictionary<ThreadInstanceKey, string>? processNames)
        {
            if (processNames is null)
                return;

            foreach (var item in AnalysisEvents.Enumerate(processNames))
                RememberProcessName(item.Key, item.Value);
        }

        public void AddContextSwitches(
            IReadOnlyDictionary<ThreadInstanceKey, long>? contextSwitches)
        {
            if (contextSwitches is null)
                return;

            foreach (var item in AnalysisEvents.Enumerate(contextSwitches))
            {
                if (!_scope.MatchesThread(item.Key) || item.Value <= 0)
                    continue;
                var projection = GetScopedProjection(item.Key);
                projection.ContextSwitches = checked(
                    projection.ContextSwitches + item.Value);
            }
        }

        public void SetEventSummary(
            long totalCSwitches,
            long? traceCSwitchCount,
            bool hasContextSwitchBlockingStacks,
            long? scopedCSwitches = null,
            long? scopedStackedSwitches = null,
            long traceIdentityUnresolvedCSwitchSideCount = 0,
            long scopedIdentityUnresolvedCSwitchSideCount = 0)
        {
            _windowCSwitchesAllThreads = totalCSwitches;
            _traceCSwitchCount = traceCSwitchCount;
            _scopedCSwitches = scopedCSwitches ??
                (_scope.Pid is null ? totalCSwitches : 0);
            _scopedStackedSwitches = scopedStackedSwitches ??
                (hasContextSwitchBlockingStacks && _scopedCSwitches > 0 ? 1 : 0);
            _traceIdentityUnresolvedCSwitchSideCount =
                traceIdentityUnresolvedCSwitchSideCount;
            _scopedIdentityUnresolvedCSwitchSideCount =
                scopedIdentityUnresolvedCSwitchSideCount;
        }

        public WaitAnalysisResponse Build(
            int top,
            int unmatchedBlockedIntervalCount,
            IEnumerable<string>? warnings,
            int? scopedUnmatchedBlockedIntervalCount = null) =>
            BuildDetailed(
                top,
                unmatchedBlockedIntervalCount,
                warnings,
                scopedUnmatchedBlockedIntervalCount).Response;

        public WaitAnalysisDetailedResult BuildDetailed(
            int top,
            int unmatchedBlockedIntervalCount,
            IEnumerable<string>? warnings,
            int? scopedUnmatchedBlockedIntervalCount = null)
        {
            RequirePositiveTop(top);
            if (unmatchedBlockedIntervalCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(unmatchedBlockedIntervalCount));
            }
            var scopedUnmatched = scopedUnmatchedBlockedIntervalCount ??
                unmatchedBlockedIntervalCount;
            if (scopedUnmatched < 0)
                throw new ArgumentOutOfRangeException(nameof(scopedUnmatchedBlockedIntervalCount));

            if (_scope.Thread is not null)
            {
                GetScopedProjection(_scope.Thread.Key);
            }

            var completeRows = _projections.Values
                .Select(ToRow)
                .OrderByDescending(row => row.BlockedUs)
                .ThenByDescending(row => row.CpuUs)
                .ThenBy(row => row.Pid)
                .ThenBy(row => row.ProcessStartUs)
                .ThenBy(row => row.Tid)
                .ThenBy(row => row.ThreadGeneration)
                .ToList();
            var candidates = completeRows;
            var rows = _scope.Thread is null
                ? candidates.Take(top).ToList()
                : candidates;

            var hasScopeContract = _scope.IncludedProcesses is not null;
            var selectedProcess = hasScopeContract
                ? _scope.Process?.Key ?? _scope.Thread?.Key.Process
                : _processScope?.SelectedProcess ?? _scope.Process?.Key;
            var scopeMode = hasScopeContract
                ? _scope.ScopeMode
                : _processScope?.ScopeMode ??
                    (_scope.Pid is null
                        ? "all_processes"
                        : _scope.AggregatesPidLifetimes
                            ? "pid_aggregate"
                            : "single_process");
            var includedProcesses = hasScopeContract
                ? _scope.IncludedProcesses!
                : _processScope?.IncludedProcesses ??
                    (_scope.Process is not null
                        ? new[] { _scope.Process.Key }
                        : candidates
                            .Select(row => new ProcessInstanceKey(row.Pid, row.ProcessStartUs))
                            .Distinct()
                            .OrderBy(process => process.Pid)
                            .ThenBy(process => process.StartUs)
                            .ToArray());
            var pidReuseObserved = _processScope?.PidReuseObserved ??
                                   _scope.PidReuseObserved;
            var hasScopedEvidence = _scopedCSwitches > 0 ||
                                    _totalBlockedUs > 0 ||
                                    _totalCpuUs > 0 ||
                                    _projections.Values.Any(
                                        projection => projection.ContextSwitches > 0);
            var capabilityStatus = hasScopedEvidence
                ? "observed"
                : _traceCSwitchCount == 0
                    ? "not_observed"
                    : "unknown";
            var noDataReason = hasScopedEvidence
                ? null
                : _traceCSwitchCount == 0
                    ? "event_class_not_observed"
                    : _scopedIdentityUnresolvedCSwitchSideCount > 0
                        ? "source_events_unattributed"
                        : "no_events_in_scope";

            var outputWarnings = warnings?.ToList() ?? new List<string>();
            if (scopeMode == "pid_aggregate")
            {
                outputWarnings.Add(
                    $"pid_aggregate: PID {_scope.Pid} matched {includedProcesses.Count} process lifetimes in the requested window; rows remain instance-separated and totals combine those lifetimes. Specify processStartUs for one lifetime.");
            }
            outputWarnings.Add(
                "total_cswitches_deprecated: TotalCSwitches is a compatibility alias for WindowCSwitchesAllThreads and is not scoped to the selected PID or thread; use ScopedCSwitches for the selected switch-out scope.");
            if (noDataReason == "event_class_not_observed")
            {
                outputWarnings.Add(
                    "event_class_not_observed: no CSwitch events were observed in the trace. " +
                    "This does not prove the CSwitch keyword was disabled; no qualifying switches may have occurred or the materialized trace may not expose that event class.");
            }
            else if (noDataReason == "no_events_in_scope")
            {
                outputWarnings.Add(
                    "no_events_in_scope: CSwitch events were observed elsewhere in the trace, but no switch-out, running, or blocked-time evidence matched the selected process/thread lifetimes and requested half-open window.");
            }
            else if (noDataReason == "source_events_unattributed")
            {
                outputWarnings.Add(
                    "source_events_unattributed: CSwitch event sides matched the lifetime-aware raw PID/TID selector and half-open query window, but thread-instance identity was unresolved or ambiguous; no scoped attribution was guessed.");
            }

            return new WaitAnalysisDetailedResult(
                new WaitAnalysisResponse(
                    Rows: rows,
                    TotalCSwitches: _windowCSwitchesAllThreads,
                    Warnings: outputWarnings,
                    TotalBlockedUs: _totalBlockedUs,
                    UnmatchedBlockedIntervalCount: unmatchedBlockedIntervalCount,
                    SelectedProcess: selectedProcess,
                    SelectedThread: _scope.Thread?.Key,
                    HasContextSwitches: _scopedCSwitches > 0,
                    HasContextSwitchBlockingStacks: _scopedStackedSwitches > 0,
                    SymbolResolutionState: "not_applicable",
                    WindowCSwitchesAllThreads: _windowCSwitchesAllThreads,
                    ScopedCSwitches: _scopedCSwitches,
                    ScopedStackedSwitches: _scopedStackedSwitches,
                    ScopedStackCoveragePct: _scopedCSwitches > 0
                        ? 100.0 * _scopedStackedSwitches / _scopedCSwitches
                        : null,
                    ScopeMode: scopeMode,
                    PidReuseObserved: pidReuseObserved,
                    IncludedProcesses: includedProcesses,
                    ScopeStatus: ProcessAnalysisScope.ResolvedStatus,
                    CapabilityStatus: capabilityStatus,
                    MatchedEventCount: _scopedCSwitches,
                    NoDataReason: noDataReason,
                    IncludedThreads: hasScopeContract
                        ? _scope.IncludedThreads ?? []
                        : _scope.Thread is null
                            ? []
                            : [new ThreadScopeCandidate(
                                _scope.Thread.Key,
                                _scope.Thread.StartUs,
                                _scope.Thread.EndUs)],
                    TraceUnmatchedBlockedIntervalCount: unmatchedBlockedIntervalCount,
                    ScopedUnmatchedBlockedIntervalCount: scopedUnmatched,
                    TraceHasContextSwitches: _traceCSwitchCount.HasValue
                        ? _traceCSwitchCount.Value > 0
                        : null,
                    TraceCSwitches: _traceCSwitchCount.GetValueOrDefault(),
                    MatchedIntervalCount: _matchedBlockedIntervalCount,
                    TraceIdentityUnresolvedCSwitchSideCount:
                        _traceIdentityUnresolvedCSwitchSideCount,
                    ScopedIdentityUnresolvedCSwitchSideCount:
                        _scopedIdentityUnresolvedCSwitchSideCount),
                completeRows);
        }

        private void ObserveSwitchOut(
            ThreadInstanceKey thread,
            string processName,
            long timestampUs)
        {
            RememberProcessName(thread, processName);
            if (!_scope.MatchesPoint(thread, timestampUs))
            {
                return;
            }

            var projection = GetScopedProjection(thread);
            projection.ContextSwitches = checked(projection.ContextSwitches + 1);
        }

        private void ObserveUnresolvedSide(
            bool identityUnresolved,
            int pid,
            int tid,
            long timestampUs)
        {
            if (!identityUnresolved)
                return;

            _traceIdentityUnresolvedCSwitchSideCount = checked(
                _traceIdentityUnresolvedCSwitchSideCount + 1);
            var matchesScope = _identities is null
                ? _scope.MatchesPoint(pid, tid, timestampUs)
                : _scope.MatchesRawUnresolvedCandidate(
                    _identities, pid, tid, timestampUs);
            if (matchesScope)
            {
                _scopedIdentityUnresolvedCSwitchSideCount = checked(
                    _scopedIdentityUnresolvedCSwitchSideCount + 1);
            }
        }

        private void RememberProcessName(
            ThreadInstanceKey thread,
            string? processName)
        {
            if (string.IsNullOrEmpty(processName))
                return;

            _processNames.TryAdd(thread, processName);
            if (_projections.TryGetValue(thread, out var projection) &&
                string.IsNullOrEmpty(projection.ProcessName))
            {
                projection.ProcessName = processName;
            }
        }

        private ThreadProjection GetScopedProjection(ThreadInstanceKey thread)
        {
            var projection = GetProjection(
                _projections, thread, _processNames);
            if (projection.ThreadStartUs == 0)
            {
                projection.ThreadStartUs = _threadStartUs?.Invoke(thread) ??
                    (_scope.Thread?.Key == thread ? _scope.Thread.StartUs : 0);
            }
            return projection;
        }
    }

    private sealed class ThreadProjection(ThreadInstanceKey key, long threadStartUs)
    {
        public ThreadInstanceKey Key { get; } = key;
        public long ThreadStartUs { get; set; } = threadStartUs;
        public string ProcessName { get; set; } = string.Empty;
        public long CpuUs { get; set; }
        public long BlockedUs { get; set; }
        public long ContextSwitches { get; set; }
        public Dictionary<string, (long BlockedUs, long Count)> WaitReasons { get; } =
            new(StringComparer.Ordinal);
    }
}
