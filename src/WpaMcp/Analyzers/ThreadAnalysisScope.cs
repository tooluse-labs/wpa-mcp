using WpaMcp.Core;

namespace WpaMcp.Analyzers;

internal readonly record struct ThreadAnalysisScope(
    TimeWindow Window,
    int? Pid,
    ProcessLifetime? Process,
    ThreadLifetime? Thread,
    bool AggregatesPidLifetimes,
    bool PidReuseObserved,
    string ScopeStatus = ProcessAnalysisScope.ResolvedStatus,
    string? DeclaredScopeMode = null,
    string? NoDataReason = null,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    IReadOnlyList<ThreadScopeCandidate>? IncludedThreads = null,
    string? ScopeWarning = null)
{
    public bool IsResolved => ScopeStatus == ProcessAnalysisScope.ResolvedStatus;

    public string ScopeMode => DeclaredScopeMode ??
        (Process is not null || Thread is not null
            ? "single_process"
            : Pid.HasValue ? "pid_aggregate" : "all_processes");

    public bool MatchesPoint(int pid, int tid, long timestampUs)
    {
        if (!IsResolved || !Window.ContainsPoint(timestampUs))
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
        if (!IsResolved || !Window.ContainsPoint(timestampUs) || !MatchesThread(thread))
            return false;
        if (Thread is not null)
            return Thread.StartUs <= timestampUs && timestampUs < Thread.EndUs;
        if (Process is not null)
            return Process.Key.StartUs <= timestampUs && timestampUs < Process.EndUs;
        return true;
    }

    public bool MatchesThread(ThreadInstanceKey thread) => IsResolved &&
        (Thread is not null ? Thread.Key == thread :
         Process is not null ? Process.Key == thread.Process :
         Pid is null || Pid.Value == thread.Process.Pid);

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

    public static ThreadAnalysisScope Materialize(
        TimeWindow window,
        int? pid,
        int? tid,
        long? processStartUs,
        long? threadStartUs,
        TraceIdentityIndex identities,
        ProcessAnalysisScope processScope,
        InstanceResolution<ThreadAnalysisScope> resolution,
        long? threadGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(processScope);

        var includedProcesses = processScope.Pid.HasValue
            ? processScope.IncludedProcesses
            : Array.Empty<ProcessInstanceKey>();
        if (!processScope.IsResolved)
        {
            var processFailureCode = processScope.ScopeStatus == ProcessAnalysisScope.AmbiguousStatus
                ? ProcessAnalysisScope.AmbiguousStatus
                : "process_instance_not_found";
            return new ThreadAnalysisScope(
                window,
                pid,
                Process: null,
                Thread: null,
                AggregatesPidLifetimes: false,
                PidReuseObserved: processScope.PidReuseObserved,
                ScopeStatus: processScope.ScopeStatus,
                DeclaredScopeMode: "unresolved",
                NoDataReason: processScope.ScopeStatus,
                IncludedProcesses: includedProcesses,
                IncludedThreads: Array.Empty<ThreadScopeCandidate>(),
                ScopeWarning: processFailureCode == ProcessAnalysisScope.AmbiguousStatus
                    ? BuildConflictingProcessWarning(
                        processScope, pid, tid, processStartUs, threadStartUs,
                        threadGeneration, window)
                    : BuildNotFoundWarning(
                        processFailureCode, pid, tid, processStartUs, threadStartUs,
                        threadGeneration, window));
        }

        if (resolution.Status == InstanceResolutionStatus.Resolved &&
            resolution.Value.HasValue)
        {
            var resolved = resolution.Value.Value;
            var includedThreads = resolved.Thread is null
                ? Array.Empty<ThreadScopeCandidate>()
                : [new ThreadScopeCandidate(
                    resolved.Thread.Key,
                    resolved.Thread.StartUs,
                    resolved.Thread.EndUs)];
            IReadOnlyList<ProcessInstanceKey> resolvedProcesses = resolved.Thread is not null
                ? [resolved.Thread.Key.Process]
                : resolved.Process is not null
                    ? [resolved.Process.Key]
                    : includedProcesses;
            var resolvedMode = resolved.Thread is not null || resolved.Process is not null
                ? "single_process"
                : processScope.ScopeMode;
            return resolved with
            {
                ScopeStatus = ProcessAnalysisScope.ResolvedStatus,
                DeclaredScopeMode = resolvedMode,
                NoDataReason = null,
                IncludedProcesses = resolvedProcesses,
                IncludedThreads = includedThreads,
                ScopeWarning = null,
            };
        }

        var candidateProcesses = resolution.Candidates
            .Select(candidate => candidate.Process?.Key ?? candidate.Thread?.Key.Process)
            .Where(candidate => candidate.HasValue)
            .Select(candidate => candidate!.Value)
            .Distinct()
            .OrderBy(candidate => candidate.Pid)
            .ThenBy(candidate => candidate.StartUs)
            .ToArray();
        var candidateThreads = resolution.Candidates
            .Where(candidate => candidate.Thread is not null)
            .Select(candidate => new ThreadScopeCandidate(
                candidate.Thread!.Key,
                candidate.Thread.StartUs,
                candidate.Thread.EndUs))
            .Distinct()
            .OrderBy(candidate => candidate.Thread.Process.Pid)
            .ThenBy(candidate => candidate.Thread.Process.StartUs)
            .ThenBy(candidate => candidate.Thread.Tid)
            .ThenBy(candidate => candidate.ThreadStartUs)
            .ThenBy(candidate => candidate.Thread.Generation)
            .ToArray();
        var selectedProcess = processScope.SelectedProcess.HasValue
            ? identities.Processes.Lifetimes.FirstOrDefault(
                lifetime => lifetime.Key == processScope.SelectedProcess.Value)
            : null;

        if (resolution.Status == InstanceResolutionStatus.Ambiguous)
        {
            var code = tid.HasValue
                ? "ambiguous_thread_instance"
                : "ambiguous_process_instance";
            var warning = BuildAmbiguousWarning(
                code,
                resolution.Candidates,
                pid,
                tid,
                processStartUs,
                threadStartUs,
                threadGeneration,
                window);
            return new ThreadAnalysisScope(
                window,
                pid,
                Process: candidateProcesses.Length == 1 ? selectedProcess : null,
                Thread: null,
                AggregatesPidLifetimes: false,
                PidReuseObserved: processScope.PidReuseObserved,
                ScopeStatus: code,
                DeclaredScopeMode: "unresolved",
                NoDataReason: code,
                IncludedProcesses: candidateProcesses,
                IncludedThreads: candidateThreads,
                ScopeWarning: warning);
        }

        var notFoundCode = tid.HasValue
            ? "thread_instance_not_found"
            : "process_instance_not_found";
        return new ThreadAnalysisScope(
            window,
            pid,
            Process: selectedProcess,
            Thread: null,
            AggregatesPidLifetimes: false,
            PidReuseObserved: processScope.PidReuseObserved,
            ScopeStatus: ProcessAnalysisScope.NotFoundStatus,
            DeclaredScopeMode: "unresolved",
            NoDataReason: ProcessAnalysisScope.NotFoundStatus,
            IncludedProcesses: includedProcesses,
            IncludedThreads: Array.Empty<ThreadScopeCandidate>(),
            ScopeWarning: BuildNotFoundWarning(
                notFoundCode, pid, tid, processStartUs, threadStartUs,
                threadGeneration, window));
    }

    private static string BuildNotFoundWarning(
        string code,
        int? pid,
        int? tid,
        long? processStartUs,
        long? threadStartUs,
        long? threadGeneration,
        TimeWindow window) =>
        $"{code}: no lifetime matched pid={Format(pid)}, processStartUs={Format(processStartUs)}, " +
        $"tid={Format(tid)}, threadStartUs={Format(threadStartUs)}, " +
        $"threadGeneration={Format(threadGeneration)} in [{window.StartUs}, {window.EndUs}).";

    private static string BuildConflictingProcessWarning(
        ProcessAnalysisScope processScope,
        int? pid,
        int? tid,
        long? processStartUs,
        long? threadStartUs,
        long? threadGeneration,
        TimeWindow window) =>
        $"{ProcessAnalysisScope.AmbiguousStatus}: conflicting observed process stop endpoints " +
        $"prevented selection for pid={Format(pid)}, processStartUs={Format(processStartUs)}, " +
        $"tid={Format(tid)}, threadStartUs={Format(threadStartUs)}, " +
        $"threadGeneration={Format(threadGeneration)} in [{window.StartUs}, {window.EndUs}); " +
        $"candidates: {string.Join(", ", processScope.IncludedProcesses)}.";

    private static string BuildAmbiguousWarning(
        string code,
        IReadOnlyList<ThreadAnalysisScope> candidates,
        int? pid,
        int? tid,
        long? processStartUs,
        long? threadStartUs,
        long? threadGeneration,
        TimeWindow window)
    {
        var selectors = candidates.Select(candidate =>
            candidate.Thread is { } thread
                ? $"pid={thread.Key.Process.Pid},processStartUs={thread.Key.Process.StartUs}," +
                  $"tid={thread.Key.Tid},threadStartUs={thread.StartUs}," +
                  $"threadGeneration={thread.Key.Generation}"
                : candidate.Process is { } process
                    ? $"pid={process.Key.Pid},processStartUs={process.Key.StartUs}"
                    : "unresolved");
        return $"{code}: selector pid={Format(pid)}, processStartUs={Format(processStartUs)}, " +
               $"tid={Format(tid)}, threadStartUs={Format(threadStartUs)}, " +
               $"threadGeneration={Format(threadGeneration)} matched multiple lifetimes " +
               $"in [{window.StartUs}, {window.EndUs}); candidates: {string.Join("; ", selectors)}.";
    }

    private static string Format<T>(T? value) where T : struct =>
        value.HasValue ? value.Value.ToString()! : "null";

    public static InstanceResolution<ThreadAnalysisScope> Resolve(
        TimeWindow window,
        int? pid,
        int? tid,
        long? processStartUs,
        long? threadStartUs,
        TraceIdentityIndex identities,
        long? threadGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(identities);
        Validation.RequireThreadSelector(
            pid, tid, processStartUs, threadStartUs, threadGeneration);

        if (!pid.HasValue)
        {
            return Resolved(new ThreadAnalysisScope(
                window, null, null, null,
                AggregatesPidLifetimes: false,
                PidReuseObserved: false));
        }

        var pidLifetimes = identities.Processes.Lifetimes
            .Where(lifetime => lifetime.Key.Pid == pid.Value)
            .ToArray();
        var processLifetimes = pidLifetimes
            .Where(lifetime =>
                lifetime.Key.StartUs < window.EndUs &&
                lifetime.EndUs > window.StartUs)
            .ToArray();
        var pidReuseObserved = pidLifetimes.Length > 1;
        var conflictingProcessLifetimes = processLifetimes
            .Where(lifetime =>
                (!processStartUs.HasValue || lifetime.Key.StartUs == processStartUs.Value) &&
                identities.Processes.ConflictingObservedEndKeys.Contains(lifetime.Key))
            .ToArray();
        if (conflictingProcessLifetimes.Length > 0)
        {
            return AmbiguousFromProcessCandidates(
                window, pid.Value, conflictingProcessLifetimes, pidReuseObserved);
        }

        if (!tid.HasValue && !processStartUs.HasValue)
        {
            return Resolved(new ThreadAnalysisScope(
                window,
                pid,
                Process: processLifetimes.Length == 1 ? processLifetimes[0] : null,
                Thread: null,
                AggregatesPidLifetimes: processLifetimes.Length > 1,
                PidReuseObserved: pidReuseObserved));
        }

        ProcessLifetime? selectedProcess = null;
        if (processStartUs.HasValue)
        {
            var matchingProcesses = processLifetimes
                .Where(lifetime => lifetime.Key.StartUs == processStartUs.Value)
                .ToArray();
            if (matchingProcesses.Length != 1)
            {
                return FromProcessCandidates(
                    window, pid.Value, matchingProcesses, pidReuseObserved);
            }
            selectedProcess = matchingProcesses[0];
        }

        if (!tid.HasValue)
        {
            return Resolved(new ThreadAnalysisScope(
                window, pid, selectedProcess, null,
                AggregatesPidLifetimes: false,
                PidReuseObserved: pidReuseObserved));
        }

        var threadLifetimes = identities.Threads.Lifetimes
            .Where(lifetime =>
                lifetime.Key.Process.Pid == pid.Value &&
                lifetime.Key.Tid == tid.Value &&
                (selectedProcess is null || lifetime.Key.Process == selectedProcess.Key) &&
                (!threadStartUs.HasValue || lifetime.StartUs == threadStartUs.Value) &&
                (!threadGeneration.HasValue ||
                 lifetime.Key.Generation == threadGeneration.Value) &&
                lifetime.Intersects(window))
            .ToArray();

        var candidates = threadLifetimes
            .SelectMany(lifetime => identities.Processes.Lifetimes
                .Where(process => process.Key == lifetime.Key.Process)
                .Select(process => new ThreadAnalysisScope(
                    window, pid, process, lifetime,
                    AggregatesPidLifetimes: false,
                    PidReuseObserved: pidReuseObserved)))
            .ToArray();

        return FromCandidates(candidates);
    }

    public static ThreadAnalysisScope ResolveRequired(
        TimeWindow window,
        int? pid,
        int? tid,
        long? processStartUs,
        long? threadStartUs,
        TraceIdentityIndex identities,
        long? threadGeneration = null)
    {
        Validation.RequireThreadSelector(
            pid, tid, processStartUs, threadStartUs, threadGeneration);

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
            window, pid, tid, processStartUs, threadStartUs, identities,
            threadGeneration);
        if (resolution.Status == InstanceResolutionStatus.Resolved &&
            resolution.Value.HasValue)
        {
            return resolution.Value.Value;
        }

        var processAmbiguity =
            resolution.Status == InstanceResolutionStatus.Ambiguous &&
            resolution.Candidates.Count > 0 &&
            resolution.Candidates.All(candidate =>
                candidate.Process is not null && candidate.Thread is null);
        var prefix = processAmbiguity
            ? "process"
            : tid.HasValue
                ? "thread"
                : "process";
        var suffix = resolution.Status == InstanceResolutionStatus.Ambiguous
            ? "ambiguous"
            : "not_found";
        throw new ThreadScopeResolutionException($"{prefix}_instance_{suffix}");
    }

    private static InstanceResolution<ThreadAnalysisScope> FromProcessCandidates(
        TimeWindow window,
        int pid,
        IReadOnlyList<ProcessLifetime> processes,
        bool pidReuseObserved) =>
        FromCandidates(processes
            .Select(process => new ThreadAnalysisScope(
                window, pid, process, null,
                AggregatesPidLifetimes: false,
                PidReuseObserved: pidReuseObserved))
            .ToArray());

    private static InstanceResolution<ThreadAnalysisScope> AmbiguousFromProcessCandidates(
        TimeWindow window,
        int pid,
        IReadOnlyList<ProcessLifetime> processes,
        bool pidReuseObserved)
    {
        var candidates = processes
            .Select(process => new ThreadAnalysisScope(
                window, pid, process, null,
                AggregatesPidLifetimes: false,
                PidReuseObserved: pidReuseObserved))
            .ToArray();
        return new InstanceResolution<ThreadAnalysisScope>(
            InstanceResolutionStatus.Ambiguous,
            null,
            candidates);
    }

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
