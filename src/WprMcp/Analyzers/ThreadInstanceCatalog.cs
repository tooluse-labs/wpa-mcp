using WprMcp.Core;

namespace WprMcp.Analyzers;

internal readonly record struct ThreadSwitchResolution(
    InstanceResolution<ThreadInstanceKey> OldThread,
    InstanceResolution<ThreadInstanceKey> NewThread);

internal sealed class ThreadInstanceCatalog
{
    private readonly Dictionary<ThreadStreamKey, long> _generations = new();
    private readonly Dictionary<ThreadStreamKey, ActiveThread> _active = new();
    private readonly List<ThreadLifetime> _closed = [];
    private readonly IReadOnlyDictionary<ProcessInstanceKey, long> _processEndUs;
    private IReadOnlyDictionary<ThreadLookupKey, ThreadLifetimeIndex> _lookupByStream =
        new Dictionary<ThreadLookupKey, ThreadLifetimeIndex>();
    private IReadOnlyDictionary<ThreadInstanceKey, ThreadLifetime> _lifetimesByKey =
        new Dictionary<ThreadInstanceKey, ThreadLifetime>();
    private bool _completed;

    public ThreadInstanceCatalog()
        : this(Array.Empty<ProcessLifetime>())
    {
    }

    internal ThreadInstanceCatalog(IEnumerable<ProcessLifetime> processes)
    {
        _processEndUs = processes
            .GroupBy(lifetime => lifetime.Key)
            .Select(group => new
            {
                Key = group.Key,
                EndTimes = group.Select(lifetime => lifetime.EndUs).Distinct().ToArray(),
            })
            .Where(item => item.EndTimes.Length == 1)
            .ToDictionary(item => item.Key, item => item.EndTimes[0]);
    }

    public IReadOnlyList<ThreadLifetime> Lifetimes { get; private set; } =
        Array.Empty<ThreadLifetime>();

    public void Start(
        ProcessInstanceKey process,
        int tid,
        long startUs,
        bool startObserved)
    {
        EnsureMutable();
        var stream = new ThreadStreamKey(process, tid);
        if (_active.Remove(stream, out var active))
        {
            Close(active, startUs, endObserved: false);
        }

        var generation = NextGeneration(stream);
        var key = new ThreadInstanceKey(process, tid, generation);
        _active.Add(stream, new ActiveThread(key, startUs, startObserved));
    }

    internal void StartIfAbsent(
        ProcessInstanceKey process,
        int tid,
        long startUs,
        bool startObserved)
    {
        EnsureMutable();
        var stream = new ThreadStreamKey(process, tid);
        if (_active.ContainsKey(stream))
            return;

        var generation = NextGeneration(stream);
        var key = new ThreadInstanceKey(process, tid, generation);
        _active.Add(stream, new ActiveThread(key, startUs, startObserved));
    }

    public void Stop(ProcessInstanceKey process, int tid, long endUs)
    {
        Stop(process, tid, endUs, endObserved: true);
    }

    internal void Stop(
        ProcessInstanceKey process,
        int tid,
        long endUs,
        bool endObserved)
    {
        EnsureMutable();
        var stream = new ThreadStreamKey(process, tid);
        if (_active.Remove(stream, out var active))
        {
            Close(active, endUs, endObserved);
            return;
        }

        var generation = NextGeneration(stream);
        Close(
            new ActiveThread(
                new ThreadInstanceKey(process, tid, generation),
                process.StartUs,
                StartObserved: false),
            endUs,
            endObserved);
    }

    public void Complete(long traceEndUs)
    {
        EnsureMutable();
        foreach (var active in _active.Values)
        {
            var inferredEndUs = _processEndUs.TryGetValue(
                active.Key.Process,
                out var processEndUs)
                ? TimeWindow.ClipEnd(traceEndUs, processEndUs)
                : traceEndUs;
            Close(active, inferredEndUs, endObserved: false);
        }

        _active.Clear();
        Lifetimes = _closed
            .OrderBy(lifetime => lifetime.Key.Process.Pid)
            .ThenBy(lifetime => lifetime.Key.Process.StartUs)
            .ThenBy(lifetime => lifetime.Key.Tid)
            .ThenBy(lifetime => lifetime.Key.Generation)
            .ToArray();
        _lookupByStream = Lifetimes
            .GroupBy(lifetime => new ThreadLookupKey(
                lifetime.Key.Process.Pid,
                lifetime.Key.Tid))
            .ToDictionary(
                group => group.Key,
                group => new ThreadLifetimeIndex(group));
        _lifetimesByKey = Lifetimes.ToDictionary(lifetime => lifetime.Key);
        _completed = true;
    }

    internal long? EndUsFor(ThreadInstanceKey key) =>
        _lifetimesByKey.TryGetValue(key, out var lifetime)
            ? lifetime.EndUs
            : null;

    public InstanceResolution<ThreadInstanceKey> Resolve(
        ThreadSelector selector,
        TimeWindow window)
    {
        var candidates = Lifetimes
            .Where(lifetime =>
                lifetime.Key.Process.Pid == selector.Pid &&
                lifetime.Key.Tid == selector.Tid &&
                (!selector.ProcessStartUs.HasValue ||
                 lifetime.Key.Process.StartUs == selector.ProcessStartUs.Value) &&
                (!selector.ThreadStartUs.HasValue ||
                 lifetime.StartUs == selector.ThreadStartUs.Value) &&
                lifetime.Intersects(window))
            .Select(lifetime => lifetime.Key)
            .ToArray();

        return candidates.Length switch
        {
            0 => new InstanceResolution<ThreadInstanceKey>(
                InstanceResolutionStatus.Unresolved,
                null,
                Array.Empty<ThreadInstanceKey>()),
            1 => new InstanceResolution<ThreadInstanceKey>(
                InstanceResolutionStatus.Resolved,
                candidates[0],
                candidates),
            _ => new InstanceResolution<ThreadInstanceKey>(
                InstanceResolutionStatus.Ambiguous,
                null,
                candidates),
        };
    }

    internal ThreadSwitchResolution ResolveSwitch(
        int oldPid,
        int oldTid,
        int newPid,
        int newTid,
        long timestampUs) =>
        new(
            ResolveAtEndpointOrPoint(oldPid, oldTid, timestampUs),
            ResolveAt(newPid, newTid, timestampUs));

    internal InstanceResolution<ThreadInstanceKey> ResolveAt(
        int pid,
        int tid,
        long timestampUs)
    {
        if (pid <= 0 || tid <= 0 ||
            !_lookupByStream.TryGetValue(new ThreadLookupKey(pid, tid), out var index))
        {
            return Unresolved();
        }

        return index.ResolveAt(timestampUs);
    }

    internal InstanceResolution<ThreadInstanceKey> ResolveAtEndpoint(
        int pid,
        int tid,
        long timestampUs,
        bool? preferredEndObserved = null)
    {
        if (pid <= 0 || tid <= 0 ||
            !_lookupByStream.TryGetValue(new ThreadLookupKey(pid, tid), out var index))
        {
            return Unresolved();
        }

        return index.ResolveAtEndpoint(timestampUs, preferredEndObserved);
    }

    private InstanceResolution<ThreadInstanceKey> ResolveAtEndpointOrPoint(
        int pid,
        int tid,
        long timestampUs)
    {
        if (pid <= 0 || tid <= 0 ||
            !_lookupByStream.TryGetValue(new ThreadLookupKey(pid, tid), out var index))
        {
            return Unresolved();
        }

        return index.ResolveAtEndpointOrPoint(timestampUs);
    }

    private long NextGeneration(ThreadStreamKey stream)
    {
        var generation = _generations.TryGetValue(stream, out var current)
            ? checked(current + 1)
            : 1;
        _generations[stream] = generation;
        return generation;
    }

    private void Close(ActiveThread active, long endUs, bool endObserved)
    {
        _closed.Add(new ThreadLifetime(
            active.Key,
            active.StartUs,
            endUs,
            active.StartObserved,
            endObserved));
    }

    private void EnsureMutable()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Thread instance catalog is already complete.");
        }
    }

    private static InstanceResolution<ThreadInstanceKey> Unresolved() =>
        new(
            InstanceResolutionStatus.Unresolved,
            null,
            Array.Empty<ThreadInstanceKey>());

    private readonly record struct ThreadStreamKey(ProcessInstanceKey Process, int Tid);

    private readonly record struct ThreadLookupKey(int Pid, int Tid);

    private readonly record struct ActiveThread(
        ThreadInstanceKey Key,
        long StartUs,
        bool StartObserved);

    private sealed class ThreadLifetimeIndex
    {
        private readonly IndexedLifetime[] _byStart;
        private readonly long[] _prefixMaxEndUs;
        private readonly IndexedLifetime[] _byEnd;

        public ThreadLifetimeIndex(IEnumerable<ThreadLifetime> lifetimes)
        {
            var entries = lifetimes
                .Select(lifetime => new IndexedLifetime(lifetime))
                .ToArray();
            _byStart = entries
                .OrderBy(entry => entry.Lifetime.StartUs)
                .ThenBy(entry => entry.Lifetime.EndUs)
                .ThenBy(entry => entry.Lifetime.Key.Process.StartUs)
                .ThenBy(entry => entry.Lifetime.Key.Generation)
                .ToArray();
            _prefixMaxEndUs = new long[_byStart.Length];
            var maxEndUs = long.MinValue;
            for (var index = 0; index < _byStart.Length; index++)
            {
                if (_byStart[index].Lifetime.EndUs > maxEndUs)
                    maxEndUs = _byStart[index].Lifetime.EndUs;
                _prefixMaxEndUs[index] = maxEndUs;
            }

            _byEnd = entries
                .OrderBy(entry => entry.Lifetime.EndUs)
                .ThenBy(entry => entry.Lifetime.StartUs)
                .ThenBy(entry => entry.Lifetime.Key.Process.StartUs)
                .ThenBy(entry => entry.Lifetime.Key.Generation)
                .ToArray();
        }

        public InstanceResolution<ThreadInstanceKey> ResolveAt(long timestampUs)
        {
            var upperExclusive = UpperBoundStart(timestampUs);
            var lowerExclusive = upperExclusive - 1;
            while (lowerExclusive >= 0 && _prefixMaxEndUs[lowerExclusive] > timestampUs)
                lowerExclusive--;

            IndexedLifetime? single = null;
            var count = 0;
            for (var index = lowerExclusive + 1; index < upperExclusive; index++)
            {
                var entry = _byStart[index];
                if (timestampUs >= entry.Lifetime.EndUs)
                    continue;
                single = entry;
                count++;
            }

            if (count == 0 || single is null)
                return Unresolved();
            if (count == 1)
                return Resolved(single);

            var candidates = new ThreadInstanceKey[count];
            var candidateIndex = 0;
            for (var index = lowerExclusive + 1; index < upperExclusive; index++)
            {
                var entry = _byStart[index];
                if (timestampUs < entry.Lifetime.EndUs)
                    candidates[candidateIndex++] = entry.Lifetime.Key;
            }

            return Ambiguous(candidates);
        }

        public InstanceResolution<ThreadInstanceKey> ResolveAtEndpoint(
            long timestampUs,
            bool? preferredEndObserved)
        {
            var start = LowerBoundEnd(timestampUs);
            var end = UpperBoundEnd(timestampUs);
            if (start == end)
                return Unresolved();

            if (preferredEndObserved.HasValue)
            {
                var preferred = ResolveEndpointRange(
                    start,
                    end,
                    preferredEndObserved.Value,
                    requireProvenance: true);
                if (preferred.Status != InstanceResolutionStatus.Unresolved)
                    return preferred;
            }

            return ResolveEndpointRange(start, end, endObserved: false, requireProvenance: false);
        }

        public InstanceResolution<ThreadInstanceKey> ResolveAtEndpointOrPoint(
            long timestampUs)
        {
            var endpoint = ResolveAtEndpoint(timestampUs, preferredEndObserved: null);
            return endpoint.Status == InstanceResolutionStatus.Unresolved
                ? ResolveAt(timestampUs)
                : endpoint;
        }

        private InstanceResolution<ThreadInstanceKey> ResolveEndpointRange(
            int start,
            int end,
            bool endObserved,
            bool requireProvenance)
        {
            IndexedLifetime? single = null;
            var count = 0;
            for (var index = start; index < end; index++)
            {
                var entry = _byEnd[index];
                if (requireProvenance && entry.Lifetime.EndObserved != endObserved)
                    continue;
                single = entry;
                count++;
            }

            if (count == 0 || single is null)
                return Unresolved();
            if (count == 1)
                return Resolved(single);

            var candidates = new ThreadInstanceKey[count];
            var candidateIndex = 0;
            for (var index = start; index < end; index++)
            {
                var entry = _byEnd[index];
                if (requireProvenance && entry.Lifetime.EndObserved != endObserved)
                    continue;
                candidates[candidateIndex++] = entry.Lifetime.Key;
            }

            return Ambiguous(candidates);
        }

        private static InstanceResolution<ThreadInstanceKey> Resolved(
            IndexedLifetime entry) =>
            new(
                InstanceResolutionStatus.Resolved,
                entry.Lifetime.Key,
                entry.SingletonCandidate);

        private static InstanceResolution<ThreadInstanceKey> Ambiguous(
            IReadOnlyList<ThreadInstanceKey> candidates) =>
            new(InstanceResolutionStatus.Ambiguous, null, candidates);

        private int UpperBoundStart(long timestampUs)
        {
            var low = 0;
            var high = _byStart.Length;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (_byStart[middle].Lifetime.StartUs <= timestampUs)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }

        private int LowerBoundEnd(long timestampUs)
        {
            var low = 0;
            var high = _byEnd.Length;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (_byEnd[middle].Lifetime.EndUs < timestampUs)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }

        private int UpperBoundEnd(long timestampUs)
        {
            var low = 0;
            var high = _byEnd.Length;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (_byEnd[middle].Lifetime.EndUs <= timestampUs)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }

        private sealed class IndexedLifetime(ThreadLifetime lifetime)
        {
            public ThreadLifetime Lifetime { get; } = lifetime;

            public IReadOnlyList<ThreadInstanceKey> SingletonCandidate { get; } =
                new[] { lifetime.Key };
        }
    }
}
