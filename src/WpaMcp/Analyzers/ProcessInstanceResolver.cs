using WpaMcp.Core;

namespace WpaMcp.Analyzers;

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
        Lifetimes = lifetimes
            .OrderBy(lifetime => lifetime.Key.Pid)
            .ThenBy(lifetime => lifetime.Key.StartUs)
            .ToArray();
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
                group => new ProcessEndpointCandidates(group));
        _singletonCandidates = Lifetimes
            .Select(lifetime => lifetime.Key)
            .Distinct()
            .ToDictionary(
                key => key,
                key => (IReadOnlyList<ProcessInstanceKey>)new[] { key });
    }

    public IReadOnlyList<ProcessLifetime> Lifetimes { get; }

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
        foreach (var lifetime in lifetimes)
        {
            if ((!processStartUs.HasValue ||
                 lifetime.Key.StartUs == processStartUs.Value) &&
                lifetime.Contains(timestampUs))
            {
                candidate = lifetime.Key;
                count++;
            }
        }

        if (count == 0)
            return Unresolved();
        if (count == 1)
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

        public ProcessEndpointCandidates(IEnumerable<ProcessLifetime> lifetimes)
        {
            _all = lifetimes.Select(lifetime => lifetime.Key).ToArray();
        }

        public InstanceResolution<ProcessInstanceKey> Resolve()
        {
            return _all.Length switch
            {
                1 => new InstanceResolution<ProcessInstanceKey>(
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
