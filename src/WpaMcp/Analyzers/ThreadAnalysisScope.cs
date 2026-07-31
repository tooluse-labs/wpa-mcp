using WpaMcp.Core;

namespace WpaMcp.Analyzers;

internal readonly record struct ThreadAnalysisScope(
    TimeWindow Window,
    int? Pid,
    ProcessLifetime? Process,
    ThreadLifetime? Thread,
    bool AggregatesPidLifetimes,
    bool PidReuseObserved)
{
    public bool MatchesPoint(int pid, int tid, long timestampUs)
    {
        if (!Window.ContainsPoint(timestampUs))
            return false;
        if (Thread is not null)
        {
            return Thread.Key.Process.Pid == pid &&
                   Thread.Key.Tid == tid &&
                   Thread.StartUs <= timestampUs && timestampUs < Thread.EndUs;
        }
        if (Process is not null)
        {
            return Process.Key.Pid == pid &&
                   Process.Key.StartUs <= timestampUs && timestampUs < Process.EndUs;
        }
        return Pid is null || Pid.Value == pid;
    }

    public bool MatchesPoint(ThreadInstanceKey thread, long timestampUs)
    {
        if (!Window.ContainsPoint(timestampUs) || !MatchesThread(thread))
            return false;
        if (Thread is not null)
            return Thread.StartUs <= timestampUs && timestampUs < Thread.EndUs;
        if (Process is not null)
            return Process.Key.StartUs <= timestampUs && timestampUs < Process.EndUs;
        return true;
    }

    public bool MatchesThread(ThreadInstanceKey thread) =>
        Thread is not null ? Thread.Key == thread :
        Process is not null ? Process.Key == thread.Process :
        Pid is null || Pid.Value == thread.Process.Pid;

    public long AccountInterval(ThreadInstanceKey thread, long startUs, long endUs)
    {
        if (!MatchesThread(thread))
            return 0;

        var lifetimeStartUs = Thread?.StartUs ?? Process?.Key.StartUs ?? 0;
        var lifetimeEndUs = Thread?.EndUs ?? Process?.EndUs ?? long.MaxValue;
        if (startUs < lifetimeStartUs)
            startUs = lifetimeStartUs;
        if (endUs > lifetimeEndUs)
            endUs = lifetimeEndUs;
        return Window.IntersectDurationUs(startUs, endUs);
    }

    public static InstanceResolution<ThreadAnalysisScope> Resolve(
        TimeWindow window,
        int? pid,
        int? tid,
        long? processStartUs,
        long? threadStartUs,
        TraceIdentityIndex identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        Validation.RequireThreadSelector(pid, tid, processStartUs, threadStartUs);

        if (!pid.HasValue)
        {
            return Resolved(new ThreadAnalysisScope(
                window, null, null, null,
                AggregatesPidLifetimes: false,
                PidReuseObserved: false));
        }

        var processLifetimes = identities.Processes.Lifetimes
            .Where(lifetime =>
                lifetime.Key.Pid == pid.Value &&
                lifetime.Key.StartUs < window.EndUs &&
                lifetime.EndUs > window.StartUs)
            .ToArray();

        if (!tid.HasValue && !processStartUs.HasValue)
        {
            return Resolved(new ThreadAnalysisScope(
                window, pid, null, null,
                AggregatesPidLifetimes: true,
                PidReuseObserved: processLifetimes.Length > 1));
        }

        ProcessLifetime? selectedProcess = null;
        if (processStartUs.HasValue)
        {
            var matchingProcesses = processLifetimes
                .Where(lifetime => lifetime.Key.StartUs == processStartUs.Value)
                .ToArray();
            if (matchingProcesses.Length != 1)
            {
                return FromProcessCandidates(window, pid.Value, matchingProcesses);
            }
            selectedProcess = matchingProcesses[0];
        }

        if (!tid.HasValue)
        {
            return Resolved(new ThreadAnalysisScope(
                window, pid, selectedProcess, null,
                AggregatesPidLifetimes: false,
                PidReuseObserved: false));
        }

        var threadLifetimes = identities.Threads.Lifetimes
            .Where(lifetime =>
                lifetime.Key.Process.Pid == pid.Value &&
                lifetime.Key.Tid == tid.Value &&
                (selectedProcess is null || lifetime.Key.Process == selectedProcess.Key) &&
                (!threadStartUs.HasValue || lifetime.StartUs == threadStartUs.Value) &&
                lifetime.Intersects(window))
            .ToArray();

        var candidates = threadLifetimes
            .SelectMany(lifetime => identities.Processes.Lifetimes
                .Where(process => process.Key == lifetime.Key.Process)
                .Select(process => new ThreadAnalysisScope(
                    window, pid, process, lifetime,
                    AggregatesPidLifetimes: false,
                    PidReuseObserved: false)))
            .ToArray();

        return FromCandidates(candidates);
    }

    public static ThreadAnalysisScope ResolveRequired(
        TimeWindow window,
        int? pid,
        int? tid,
        long? processStartUs,
        long? threadStartUs,
        TraceIdentityIndex identities)
    {
        Validation.RequireThreadSelector(pid, tid, processStartUs, threadStartUs);

        if (pid.HasValue && processStartUs.HasValue)
        {
            var processCandidateCount = identities.Processes.Lifetimes.Count(lifetime =>
                lifetime.Key.Pid == pid.Value &&
                lifetime.Key.StartUs == processStartUs.Value &&
                lifetime.Key.StartUs < window.EndUs &&
                lifetime.EndUs > window.StartUs);
            if (processCandidateCount != 1)
            {
                throw new ThreadScopeResolutionException(
                    processCandidateCount == 0
                        ? "process_instance_not_found"
                        : "ambiguous_process_instance");
            }
        }

        var resolution = Resolve(
            window, pid, tid, processStartUs, threadStartUs, identities);
        if (resolution.Status == InstanceResolutionStatus.Resolved &&
            resolution.Value.HasValue)
        {
            return resolution.Value.Value;
        }

        var prefix = tid.HasValue ? "thread" : "process";
        var suffix = resolution.Status == InstanceResolutionStatus.Ambiguous
            ? "ambiguous"
            : "not_found";
        throw new ThreadScopeResolutionException($"{prefix}_instance_{suffix}");
    }

    private static InstanceResolution<ThreadAnalysisScope> FromProcessCandidates(
        TimeWindow window,
        int pid,
        IReadOnlyList<ProcessLifetime> processes) =>
        FromCandidates(processes
            .Select(process => new ThreadAnalysisScope(
                window, pid, process, null,
                AggregatesPidLifetimes: false,
                PidReuseObserved: false))
            .ToArray());

    private static InstanceResolution<ThreadAnalysisScope> Resolved(
        ThreadAnalysisScope scope) =>
        new(InstanceResolutionStatus.Resolved, scope, [scope]);

    private static InstanceResolution<ThreadAnalysisScope> FromCandidates(
        IReadOnlyList<ThreadAnalysisScope> candidates) => candidates.Count switch
        {
            0 => new InstanceResolution<ThreadAnalysisScope>(
                InstanceResolutionStatus.Unresolved,
                null,
                Array.Empty<ThreadAnalysisScope>()),
            1 => Resolved(candidates[0]),
            _ => new InstanceResolution<ThreadAnalysisScope>(
                InstanceResolutionStatus.Ambiguous,
                null,
                candidates),
        };
}

internal sealed class ThreadScopeResolutionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
