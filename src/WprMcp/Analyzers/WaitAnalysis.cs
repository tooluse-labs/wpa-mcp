using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Analyzers;

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
        var resolution = ThreadAnalysisScope.Resolve(
            window, pid, tid: null, processStartUs: null, threadStartUs: null,
            identities);
        if (resolution.Status != InstanceResolutionStatus.Resolved ||
            !resolution.Value.HasValue)
        {
            return EmptyResolutionFailure(resolution.Status);
        }

        return Analyze(trace, top, resolution.Value.Value);
    }

    internal static WaitAnalysisResponse Analyze(
        TraceLog trace,
        int top,
        ThreadAnalysisScope scope)
    {
        RequirePositiveTop(top);
        var identities = TraceIdentityIndex.For(trace);
        var projection = new WaitProjectionAccumulator(scope);
        var stream = SchedulerIntervalTraceReader.Read(trace, identities, [projection]);
        var warnings = BuildSchedulerWarnings(
            stream.Completion, stream.IdentityDiagnosticCount);
        return projection.Build(
            top,
            stream.Completion.UnmatchedBlockedIntervalCount,
            warnings);
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
        bool hasContextSwitchBlockingStacks = false)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        RequirePositiveTop(top);
        if (unmatchedBlockedIntervalCount < 0)
            throw new ArgumentOutOfRangeException(nameof(unmatchedBlockedIntervalCount));
        if (totalCSwitches < 0)
            throw new ArgumentOutOfRangeException(nameof(totalCSwitches));
        if (traceCSwitchCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(traceCSwitchCount));

        var projection = new WaitProjectionAccumulator(scope);
        projection.AddProcessNames(processNames);
        foreach (var interval in intervals)
            projection.OnBlocked(interval);
        foreach (var interval in runningIntervals ?? Array.Empty<RunningInterval>())
            projection.OnRunning(interval);
        projection.AddContextSwitches(contextSwitches);
        projection.SetEventSummary(
            totalCSwitches,
            traceCSwitchCount,
            hasContextSwitchBlockingStacks);
        return projection.Build(top, unmatchedBlockedIntervalCount, warnings);
    }

    private static WaitAnalysisResponse EmptyResolutionFailure(
        InstanceResolutionStatus status) =>
        new(
            Rows: Array.Empty<WaitAnalysisRow>(),
            TotalCSwitches: 0,
            Warnings: [$"thread_scope_{status.ToString().ToLowerInvariant()}"],
            TotalBlockedUs: 0,
            UnmatchedBlockedIntervalCount: 0,
            SelectedProcess: null,
            SelectedThread: null);

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
                $"scheduler_identity_unresolved: {unresolvedIdentityCount:N0} event-side identity resolution(s) were unavailable or ambiguous.");
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
        Dictionary<RawThreadKey, ThreadProjection> projections,
        ThreadInstanceKey thread,
        IReadOnlyDictionary<ThreadInstanceKey, string>? processNames)
    {
        var key = new RawThreadKey(thread.Process.Pid, thread.Tid);
        if (!projections.TryGetValue(key, out var projection))
        {
            projection = new ThreadProjection(key);
            projections.Add(key, projection);
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

    private static WaitAnalysisRow ToRow(ThreadProjection projection, int reasonTop)
    {
        var reasons = projection.WaitReasons
            .OrderByDescending(item => item.Value.BlockedUs)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Take(reasonTop)
            .Select(item => new WaitReasonBucket(
                item.Key, item.Value.BlockedUs, item.Value.Count))
            .ToList();
        return new WaitAnalysisRow(
            projection.Key.Pid,
            projection.ProcessName,
            projection.Key.Tid,
            projection.CpuUs,
            projection.BlockedUs,
            projection.CpuUs > 0
                ? (double)projection.BlockedUs / projection.CpuUs
                : null,
            projection.ContextSwitches,
            reasons);
    }

    internal sealed class WaitProjectionAccumulator :
        ISchedulerIntervalSink,
        ISchedulerEventSink
    {
        private readonly ThreadAnalysisScope _scope;
        private readonly Dictionary<RawThreadKey, ThreadProjection> _projections = new();
        private readonly Dictionary<ThreadInstanceKey, string> _processNames = new();
        private long _totalBlockedUs;
        private long _totalCpuUs;
        private long _totalCSwitches;
        private long? _traceCSwitchCount = 0;
        private bool _hasContextSwitchBlockingStacks;

        public WaitProjectionAccumulator(ThreadAnalysisScope scope)
        {
            _scope = scope;
        }

        public void OnRunning(in RunningInterval interval)
        {
            var accountedUs = _scope.AccountInterval(
                interval.Thread, interval.StartUs, interval.EndUs);
            if (accountedUs <= 0)
                return;

            _totalCpuUs = checked(_totalCpuUs + accountedUs);
            var projection = GetProjection(
                _projections, interval.Thread, _processNames);
            projection.CpuUs = checked(projection.CpuUs + accountedUs);
        }

        public void OnBlocked(in BlockedInterval interval)
        {
            var accountedUs = _scope.AccountInterval(
                interval.Thread, interval.StartUs, interval.EndUs);
            if (accountedUs <= 0)
                return;

            _totalBlockedUs = checked(_totalBlockedUs + accountedUs);
            var projection = GetProjection(
                _projections, interval.Thread, _processNames);
            projection.BlockedUs = checked(projection.BlockedUs + accountedUs);
            var previous = projection.WaitReasons.GetValueOrDefault(interval.WaitReason);
            projection.WaitReasons[interval.WaitReason] = (
                checked(previous.BlockedUs + accountedUs),
                checked(previous.Count + 1));
        }

        public void OnContextSwitch(in SchedulerSwitchObservation observation)
        {
            _traceCSwitchCount = checked(_traceCSwitchCount.GetValueOrDefault() + 1);
            if (_scope.Window.ContainsPoint(observation.TimestampUs))
                _totalCSwitches = checked(_totalCSwitches + 1);
            if (observation.BlockingStack != CallStackIndex.Invalid)
                _hasContextSwitchBlockingStacks = true;

            if (observation.OldThread.HasValue)
            {
                ObserveThread(
                    observation.OldThread.Value,
                    observation.OldProcessName,
                    observation.TimestampUs);
            }
            if (observation.NewThread.HasValue)
            {
                ObserveThread(
                    observation.NewThread.Value,
                    observation.NewProcessName,
                    observation.TimestampUs);
            }
        }

        public void AddProcessNames(
            IReadOnlyDictionary<ThreadInstanceKey, string>? processNames)
        {
            if (processNames is null)
                return;

            foreach (var item in processNames)
                RememberProcessName(item.Key, item.Value);
        }

        public void AddContextSwitches(
            IReadOnlyDictionary<ThreadInstanceKey, long>? contextSwitches)
        {
            if (contextSwitches is null)
                return;

            foreach (var item in contextSwitches)
            {
                if (!_scope.MatchesThread(item.Key) || item.Value <= 0)
                    continue;
                var projection = GetProjection(
                    _projections, item.Key, _processNames);
                projection.ContextSwitches = checked(
                    projection.ContextSwitches + item.Value);
            }
        }

        public void SetEventSummary(
            long totalCSwitches,
            long? traceCSwitchCount,
            bool hasContextSwitchBlockingStacks)
        {
            _totalCSwitches = totalCSwitches;
            _traceCSwitchCount = traceCSwitchCount;
            _hasContextSwitchBlockingStacks = hasContextSwitchBlockingStacks;
        }

        public WaitAnalysisResponse Build(
            int top,
            int unmatchedBlockedIntervalCount,
            IEnumerable<string>? warnings)
        {
            RequirePositiveTop(top);
            if (unmatchedBlockedIntervalCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(unmatchedBlockedIntervalCount));
            }

            if (_scope.Thread is not null)
            {
                GetProjection(
                    _projections, _scope.Thread.Key, _processNames);
            }

            var reasonTop = _scope.Thread is null ? 5 : top;
            var candidates = _projections.Values
                .Select(projection => ToRow(projection, reasonTop))
                .OrderByDescending(row => row.BlockedUs)
                .ThenByDescending(row => row.CpuUs)
                .ThenBy(row => row.Pid)
                .ThenBy(row => row.Tid)
                .ToList();
            var rows = _scope.Thread is null
                ? candidates.Take(top).ToList()
                : candidates;

            var outputWarnings = warnings?.ToList() ?? new List<string>();
            if (_scope.PidReuseObserved)
            {
                outputWarnings.Add(
                    "ambiguous_process_instance: pid-only scope aggregates multiple process lifetimes.");
            }
            if (_traceCSwitchCount == 0)
            {
                outputWarnings.Add(
                    "No CSwitch events found. The capture profile must include the CSwitch keyword. " +
                    "Default WPR 'CPU' / 'CPU.light' profiles include it; some custom .wprp files may not.");
            }
            else if (_traceCSwitchCount > 0 && _totalCSwitches == 0 &&
                     _totalBlockedUs == 0 && _totalCpuUs == 0 &&
                     _projections.Values.All(
                         projection => projection.ContextSwitches == 0))
            {
                outputWarnings.Add(
                    "CSwitch events were present in the trace, but none landed inside the requested time window.");
            }

            return new WaitAnalysisResponse(
                Rows: rows,
                TotalCSwitches: _totalCSwitches,
                Warnings: outputWarnings,
                TotalBlockedUs: _totalBlockedUs,
                UnmatchedBlockedIntervalCount: unmatchedBlockedIntervalCount,
                SelectedProcess: _scope.Process?.Key,
                SelectedThread: _scope.Thread?.Key,
                HasContextSwitches: _traceCSwitchCount > 0,
                HasContextSwitchBlockingStacks: _hasContextSwitchBlockingStacks,
                SymbolResolutionState: "not_applicable");
        }

        private void ObserveThread(
            ThreadInstanceKey thread,
            string processName,
            long timestampUs)
        {
            RememberProcessName(thread, processName);
            if (!_scope.MatchesPoint(thread, timestampUs))
            {
                return;
            }

            var projection = GetProjection(
                _projections, thread, _processNames);
            projection.ContextSwitches = checked(projection.ContextSwitches + 1);
        }

        private void RememberProcessName(
            ThreadInstanceKey thread,
            string? processName)
        {
            if (string.IsNullOrEmpty(processName))
                return;

            _processNames.TryAdd(thread, processName);
            var key = new RawThreadKey(thread.Process.Pid, thread.Tid);
            if (_projections.TryGetValue(key, out var projection) &&
                string.IsNullOrEmpty(projection.ProcessName))
            {
                projection.ProcessName = processName;
            }
        }
    }

    private readonly record struct RawThreadKey(int Pid, int Tid);

    private sealed class ThreadProjection(RawThreadKey key)
    {
        public RawThreadKey Key { get; } = key;
        public string ProcessName { get; set; } = string.Empty;
        public long CpuUs { get; set; }
        public long BlockedUs { get; set; }
        public long ContextSwitches { get; set; }
        public Dictionary<string, (long BlockedUs, long Count)> WaitReasons { get; } =
            new(StringComparer.Ordinal);
    }
}
