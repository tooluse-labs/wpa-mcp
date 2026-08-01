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

    public static ProcessAnalysisScope Resolve(
        TimeWindow window,
        int? pid,
        long? processStartUs,
        TraceIdentityIndex identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        return Resolve(window, pid, processStartUs, identities.Processes.Lifetimes);
    }

    internal static ProcessAnalysisScope Resolve(
        TimeWindow window,
        int? pid,
        long? processStartUs,
        IEnumerable<ProcessLifetime> lifetimes)
    {
        ArgumentNullException.ThrowIfNull(lifetimes);
        if (processStartUs.HasValue && !pid.HasValue)
        {
            throw new ArgumentException("processStartUs requires pid.", nameof(processStartUs));
        }

        var all = lifetimes
            .GroupBy(lifetime => lifetime.Key)
            .Select(group => group.OrderByDescending(lifetime => lifetime.EndUs).First())
            .OrderBy(lifetime => lifetime.Key.Pid)
            .ThenBy(lifetime => lifetime.Key.StartUs)
            .ToArray();
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
