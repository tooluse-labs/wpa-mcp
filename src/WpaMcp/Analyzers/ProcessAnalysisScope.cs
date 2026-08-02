using WpaMcp.Core;

namespace WpaMcp.Analyzers;

/// <summary>
/// Resolves a process/window selector to the exact process lifetimes that may
/// contribute events to an analysis. A PID-only selector intentionally permits
/// aggregation when more than one lifetime intersects the window; callers must
/// surface <see cref="ScopeMode"/> so that aggregation is never mistaken for a
/// single process instance.
/// </summary>
internal sealed class ProcessAnalysisScope
{
    public const string ResolvedStatus = "ok";
    public const string NotFoundStatus = "scope_not_found";
    public const string AmbiguousStatus = "ambiguous_process_instance";
    public const string ProcessStartRequiredStatus = "process_start_required";

    private readonly IReadOnlySet<ProcessInstanceKey> _includedProcessSet;

    private ProcessAnalysisScope(
        TimeWindow window,
        int? pid,
        long? processStartUs,
        ProcessInstanceKey? selectedProcess,
        string scopeMode,
        string scopeStatus,
        bool pidReuseObserved,
        IReadOnlyList<ProcessInstanceKey> includedProcesses)
    {
        Window = window;
        Pid = pid;
        ProcessStartUs = processStartUs;
        SelectedProcess = selectedProcess;
        ScopeMode = scopeMode;
        ScopeStatus = scopeStatus;
        PidReuseObserved = pidReuseObserved;
        IncludedProcesses = includedProcesses;
        _includedProcessSet = includedProcesses.ToHashSet();
    }

    public TimeWindow Window { get; }

    public int? Pid { get; }

    public long? ProcessStartUs { get; }

    public ProcessInstanceKey? SelectedProcess { get; }

    public string ScopeMode { get; }

    public string ScopeStatus { get; }

    public bool IsResolved => ScopeStatus == ResolvedStatus;

    internal static string ResolutionFailureWarning(string scopeStatus) =>
        scopeStatus == AmbiguousStatus
            ? "ambiguous_process_instance: overlapping process lifetimes or conflicting observed stop endpoints prevent safe process-lifetime attribution; no scoped events were attributed."
            : scopeStatus == ProcessStartRequiredStatus
                ? "process_start_required: multiple non-conflicting process lifetimes matched, but this tool requires one exact process instance; retry with a candidate processStartUs."
            : scopeStatus == NotFoundStatus
                ? "scope_not_found: no process lifetime matched the requested selector and half-open window."
                : $"{scopeStatus}: the requested process scope could not be resolved safely; no scoped events were attributed.";

    /// <summary>
    /// Converts a successfully resolved PID aggregate into a structured selector
    /// failure for analyzers whose output would be misleading across lifetimes.
    /// Candidate keys are retained so callers can replay an exact instance.
    /// </summary>
    internal ProcessAnalysisScope RequireSingleProcess()
    {
        if (!IsResolved || ScopeMode != "pid_aggregate" || !Pid.HasValue)
            return this;

        return new ProcessAnalysisScope(
            Window,
            Pid,
            ProcessStartUs,
            selectedProcess: null,
            scopeMode: "unresolved",
            scopeStatus: ProcessStartRequiredStatus,
            PidReuseObserved,
            IncludedProcesses);
    }

    internal static ArgumentException ProcessStartRequiredException(
        int pid,
        IEnumerable<ProcessInstanceKey> candidates,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var starts = candidates
            .Where(candidate => candidate.Pid == pid)
            .Select(candidate => candidate.StartUs)
            .Distinct()
            .Order()
            .ToArray();
        return new ArgumentException(
            $"{ProcessStartRequiredStatus}: PID {pid} has multiple non-conflicting lifetimes; " +
            $"this tool requires one exact process instance, so specify processStartUs. " +
            $"candidates=[{string.Join(", ", starts)}]",
            parameterName);
    }

    /// <summary>
    /// True when the selected PID (or any PID for an all-process scope) has more
    /// than one lifetime anywhere in the trace, even if the window includes one.
    /// </summary>
    public bool PidReuseObserved { get; }

    public IReadOnlyList<ProcessInstanceKey> IncludedProcesses { get; }

    /// <summary>
    /// Applies this selector to an event that does not need process-instance
    /// identity in its output. An all-process query preserves events whose PID
    /// cannot be resolved, while PID-scoped queries require an included lifetime.
    /// </summary>
    public bool MatchesEvent(
        TraceIdentityIndex identities,
        int eventPid,
        long timestampUs)
    {
        ArgumentNullException.ThrowIfNull(identities);
        if (!IsResolved || !Window.ContainsPoint(timestampUs))
            return false;
        if (!Pid.HasValue)
            return true;
        return TryResolveEventProcess(identities, eventPid, timestampUs, out _);
    }

    /// <summary>
    /// Tests whether an unresolved raw process event could belong to this scope
    /// without guessing an instance. PID scopes require the timestamp to fall
    /// within an included lifetime; all-process scopes accept any in-window PID.
    /// Stop-style endpoints may occur exactly at a lifetime's exclusive end.
    /// </summary>
    internal bool MatchesRawUnresolvedCandidate(
        TraceIdentityIndex identities,
        int eventPid,
        long timestampUs,
        bool atEndpoint = false)
    {
        ArgumentNullException.ThrowIfNull(identities);
        if (!IsResolved || !Window.ContainsPoint(timestampUs))
            return false;
        if (!Pid.HasValue)
            return true;
        if (Pid.Value != eventPid)
            return false;

        foreach (var key in IncludedProcesses)
        {
            AnalysisEvents.ThrowIfCancellationRequested();
            foreach (var lifetime in identities.Processes.FindExact(key))
            {
                AnalysisEvents.ThrowIfCancellationRequested();
                if (lifetime.Contains(timestampUs) ||
                    (atEndpoint && timestampUs == lifetime.EndUs &&
                     timestampUs >= lifetime.Key.StartUs))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static ProcessAnalysisScope Resolve(
        TimeWindow window,
        int? pid,
        long? processStartUs,
        TraceIdentityIndex identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        return ResolveNormalized(
            window,
            pid,
            processStartUs,
            identities.Processes.Lifetimes,
            identities.Processes.ConflictingObservedEndKeys);
    }

    internal static ProcessAnalysisScope Resolve(
        TimeWindow window,
        int? pid,
        long? processStartUs,
        IEnumerable<ProcessLifetime> lifetimes)
    {
        ArgumentNullException.ThrowIfNull(lifetimes);
        var normalization = ProcessInstanceResolver.Normalize(lifetimes);
        return ResolveNormalized(
            window,
            pid,
            processStartUs,
            normalization.Lifetimes,
            normalization.ConflictingObservedEndKeys);
    }

    private static ProcessAnalysisScope ResolveNormalized(
        TimeWindow window,
        int? pid,
        long? processStartUs,
        IReadOnlyList<ProcessLifetime> lifetimes,
        IReadOnlySet<ProcessInstanceKey> conflictingObservedEndKeys)
    {
        if (processStartUs.HasValue && !pid.HasValue)
        {
            throw new ArgumentException("processStartUs requires pid.", nameof(processStartUs));
        }

        var all = lifetimes;
        var pidReuseObserved = pid.HasValue
            ? all.Count(lifetime => lifetime.Key.Pid == pid.Value) > 1
            : all.GroupBy(lifetime => lifetime.Key.Pid).Any(group => group.Count() > 1);
        var included = all
            .Where(lifetime =>
                (!pid.HasValue || lifetime.Key.Pid == pid.Value) &&
                (!processStartUs.HasValue || lifetime.Key.StartUs == processStartUs.Value) &&
                lifetime.Key.StartUs < window.EndUs &&
                lifetime.EndUs > window.StartUs)
            .Select(lifetime => lifetime.Key)
            .ToArray();

        if (pid.HasValue && included.Length == 0)
        {
            return new ProcessAnalysisScope(
                window,
                pid,
                processStartUs,
                selectedProcess: null,
                scopeMode: "unresolved",
                scopeStatus: NotFoundStatus,
                pidReuseObserved,
                included);
        }

        if (pid.HasValue)
        {
            var pidLifetimesInWindow = all
                .Where(lifetime =>
                    lifetime.Key.Pid == pid.Value &&
                    lifetime.Key.StartUs < window.EndUs &&
                    lifetime.EndUs > window.StartUs)
                .OrderBy(lifetime => lifetime.Key.StartUs)
                .ThenBy(lifetime => lifetime.EndUs)
                .ToArray();
            var overlappingKeys = OverlappingKeys(pidLifetimesInWindow, window);
            if (overlappingKeys.Count > 0)
            {
                return new ProcessAnalysisScope(
                    window,
                    pid,
                    processStartUs,
                    selectedProcess: null,
                    scopeMode: "unresolved",
                    scopeStatus: AmbiguousStatus,
                    pidReuseObserved,
                    overlappingKeys);
            }
        }

        if (pid.HasValue && included.Any(conflictingObservedEndKeys.Contains))
        {
            return new ProcessAnalysisScope(
                window,
                pid,
                processStartUs,
                selectedProcess: null,
                scopeMode: "unresolved",
                scopeStatus: AmbiguousStatus,
                pidReuseObserved,
                included);
        }

        var selected = pid.HasValue && included.Length == 1
            ? included[0]
            : (ProcessInstanceKey?)null;
        var scopeMode = !pid.HasValue
            ? "all_processes"
            : included.Length == 1
                ? "single_process"
                : "pid_aggregate";
        return new ProcessAnalysisScope(
            window,
            pid,
            processStartUs,
            selected,
            scopeMode,
            ResolvedStatus,
            pidReuseObserved,
            included);
    }

    private static IReadOnlyList<ProcessInstanceKey> OverlappingKeys(
        IReadOnlyList<ProcessLifetime> lifetimes,
        TimeWindow window)
    {
        var overlapping = new HashSet<ProcessInstanceKey>();
        for (var leftIndex = 0; leftIndex < lifetimes.Count; leftIndex++)
        {
            AnalysisEvents.ThrowIfCancellationRequested();
            var left = lifetimes[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < lifetimes.Count; rightIndex++)
            {
                AnalysisEvents.ThrowIfCancellationRequested();
                var right = lifetimes[rightIndex];
                if (right.Key.StartUs >= left.EndUs)
                    break;
                var overlapStart = TimeWindow.ClipStart(
                    TimeWindow.ClipStart(left.Key.StartUs, right.Key.StartUs),
                    window.StartUs);
                var overlapEnd = TimeWindow.ClipEnd(
                    TimeWindow.ClipEnd(left.EndUs, right.EndUs),
                    window.EndUs);
                if (overlapStart < overlapEnd)
                {
                    overlapping.Add(left.Key);
                    overlapping.Add(right.Key);
                }
            }
        }
        return overlapping
            .OrderBy(key => key.StartUs)
            .ToArray();
    }

    /// <summary>
    /// Returns true only when the event is inside both the half-open requested
    /// window and one of this scope's included process lifetimes.
    /// </summary>
    public bool TryResolveEventProcess(
        TraceIdentityIndex identities,
        int eventPid,
        long timestampUs,
        out ProcessInstanceKey process)
    {
        ArgumentNullException.ThrowIfNull(identities);
        process = default;
        if (!IsResolved ||
            !Window.ContainsPoint(timestampUs) ||
            (Pid.HasValue && eventPid != Pid.Value))
        {
            return false;
        }

        var resolution = identities.Processes.Resolve(
            eventPid,
            timestampUs,
            SelectedProcess?.StartUs);
        if (resolution.Status != InstanceResolutionStatus.Resolved ||
            !resolution.Value.HasValue ||
            !_includedProcessSet.Contains(resolution.Value.Value))
        {
            return false;
        }

        process = resolution.Value.Value;
        return true;
    }
}
