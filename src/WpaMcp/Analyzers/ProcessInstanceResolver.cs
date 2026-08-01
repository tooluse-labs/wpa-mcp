using WpaMcp.Core;

namespace WpaMcp.Analyzers;

internal sealed record ProcessLifetimeNormalization(
    IReadOnlyList<ProcessLifetime> Lifetimes,
    IReadOnlySet<ProcessInstanceKey> ConflictingObservedEndKeys);

internal sealed class ProcessInstanceResolver
{
    private readonly IReadOnlyDictionary<int, IReadOnlyList<ProcessLifetime>> _lifetimesByPid;
    private readonly IReadOnlyDictionary<ProcessInstanceKey, IReadOnlyList<ProcessLifetime>>
        _lifetimesByKey;
    private readonly IReadOnlyDictionary<ProcessEndpointKey, ProcessEndpointCandidates>
        _endpointCandidates;
    private readonly IReadOnlyDictionary<ProcessInstanceKey, IReadOnlyList<ProcessInstanceKey>>
        _singletonCandidates;

    public ProcessInstanceResolver(IEnumerable<ProcessLifetime> lifetimes)
    {
        var normalization = Normalize(lifetimes);
        Lifetimes = normalization.Lifetimes;
        ConflictingObservedEndKeys = normalization.ConflictingObservedEndKeys;
        _lifetimesByPid = Lifetimes
            .GroupBy(lifetime => lifetime.Key.Pid)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ProcessLifetime>)group.ToArray());
        _lifetimesByKey = Lifetimes
            .GroupBy(lifetime => lifetime.Key)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ProcessLifetime>)group.ToArray());
        _endpointCandidates = Lifetimes
            .GroupBy(lifetime => new ProcessEndpointKey(
                lifetime.Key.Pid,
                lifetime.EndUs))
            .ToDictionary(
                group => group.Key,
                group => new ProcessEndpointCandidates(
                    group,
                    group.Any(lifetime =>
                        ConflictingObservedEndKeys.Contains(lifetime.Key))));
        _singletonCandidates = Lifetimes
            .Select(lifetime => lifetime.Key)
            .Distinct()
            .ToDictionary(
                key => key,
                key => (IReadOnlyList<ProcessInstanceKey>)new[] { key });
    }

    public IReadOnlyList<ProcessLifetime> Lifetimes { get; }

    public IReadOnlySet<ProcessInstanceKey> ConflictingObservedEndKeys { get; }

    internal static ProcessLifetimeNormalization Normalize(
        IEnumerable<ProcessLifetime> lifetimes)
    {
        ArgumentNullException.ThrowIfNull(lifetimes);
        var normalized = new List<ProcessLifetime>();
        var conflicts = new HashSet<ProcessInstanceKey>();

        foreach (var group in lifetimes.GroupBy(lifetime => lifetime.Key))
        {
            var entries = group.ToArray();
            var observedEnds = entries
                .Where(lifetime => lifetime.EndObserved)
                .Select(lifetime => lifetime.EndUs)
                .Distinct()
                .Order()
                .ToArray();
            ProcessLifetime selected;
            if (observedEnds.Length > 0)
            {
                var safeObservedEnd = observedEnds[0];
                if (observedEnds.Length > 1)
                    conflicts.Add(group.Key);
                selected = entries.First(lifetime =>
                    lifetime.EndObserved && lifetime.EndUs == safeObservedEnd);
            }
            else
            {
                selected = entries
                    .OrderByDescending(lifetime => lifetime.EndUs)
                    .ThenByDescending(lifetime => lifetime.EndFromRundown)
                    .First();
            }

            normalized.Add(selected with
            {
                StartObserved = entries.Any(lifetime => lifetime.StartObserved),
            });
        }

        return new ProcessLifetimeNormalization(
            normalized
                .OrderBy(lifetime => lifetime.Key.Pid)
                .ThenBy(lifetime => lifetime.Key.StartUs)
                .ToArray(),
            conflicts);
    }

    public IReadOnlyList<ProcessLifetime> FindExact(ProcessInstanceKey key) =>
        _lifetimesByKey.TryGetValue(key, out var lifetimes)
            ? lifetimes
            : Array.Empty<ProcessLifetime>();

    public InstanceResolution<ProcessInstanceKey> Resolve(
        int pid,
        long timestampUs,
        long? processStartUs)
    {
        if (!_lifetimesByPid.TryGetValue(pid, out var lifetimes))
        {
            return Unresolved();
        }

        ProcessInstanceKey candidate = default;
        var count = 0;
        var hasConflictingObservedEnd = false;
        foreach (var lifetime in lifetimes)
        {
            if ((!processStartUs.HasValue ||
                 lifetime.Key.StartUs == processStartUs.Value) &&
                lifetime.Contains(timestampUs))
            {
                candidate = lifetime.Key;
                count++;
                hasConflictingObservedEnd |=
                    ConflictingObservedEndKeys.Contains(lifetime.Key);
            }
        }

        if (count == 0)
            return Unresolved();
        if (count == 1 && !hasConflictingObservedEnd)
        {
            return new InstanceResolution<ProcessInstanceKey>(
                InstanceResolutionStatus.Resolved,
                candidate,
                _singletonCandidates[candidate]);
        }

        var candidates = new ProcessInstanceKey[count];
        var candidateIndex = 0;
        foreach (var lifetime in lifetimes)
        {
            if ((!processStartUs.HasValue ||
                 lifetime.Key.StartUs == processStartUs.Value) &&
                lifetime.Contains(timestampUs))
            {
                candidates[candidateIndex++] = lifetime.Key;
            }
        }

        return new InstanceResolution<ProcessInstanceKey>(
            InstanceResolutionStatus.Ambiguous,
            null,
            candidates);
    }

    public InstanceResolution<ProcessInstanceKey> ResolveAtEndpoint(
        int pid,
        long timestampUs)
    {
        if (_endpointCandidates.TryGetValue(
                new ProcessEndpointKey(pid, timestampUs),
                out var endpoint))
        {
            return endpoint.Resolve();
        }

        return Resolve(pid, timestampUs, processStartUs: null);
    }

    private static InstanceResolution<ProcessInstanceKey> Unresolved() =>
        new(
            InstanceResolutionStatus.Unresolved,
            null,
            Array.Empty<ProcessInstanceKey>());

    private readonly record struct ProcessEndpointKey(int Pid, long EndUs);

    private sealed class ProcessEndpointCandidates
    {
        private readonly ProcessInstanceKey[] _all;
        private readonly bool _hasConflictingObservedEnd;

        public ProcessEndpointCandidates(
            IEnumerable<ProcessLifetime> lifetimes,
            bool hasConflictingObservedEnd)
        {
            _all = lifetimes.Select(lifetime => lifetime.Key).ToArray();
            _hasConflictingObservedEnd = hasConflictingObservedEnd;
        }

        public InstanceResolution<ProcessInstanceKey> Resolve()
        {
            return (_all.Length, _hasConflictingObservedEnd) switch
            {
                (1, false) => new InstanceResolution<ProcessInstanceKey>(
                    InstanceResolutionStatus.Resolved,
                    _all[0],
                    _all),
                _ => new InstanceResolution<ProcessInstanceKey>(
                    InstanceResolutionStatus.Ambiguous,
                    null,
                    _all),
            };
        }
    }
}
