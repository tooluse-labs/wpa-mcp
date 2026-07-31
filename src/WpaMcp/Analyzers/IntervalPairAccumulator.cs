namespace WpaMcp.Analyzers;

internal readonly record struct PendingIntervalStart<TKey, TStart>(
    TKey Key,
    long TimeUs,
    TStart Data) where TKey : notnull;

internal readonly record struct UnmatchedIntervalStop<TKey, TStop>(
    TKey Key,
    long TimeUs,
    TStop Data) where TKey : notnull;

internal readonly record struct InvalidPairedInterval<TKey, TStart, TStop>(
    TKey Key,
    long StartUs,
    long EndUs,
    TStart StartData,
    TStop StopData) where TKey : notnull;

internal readonly record struct PairedInterval<TKey, TStart, TStop>(
    TKey Key,
    long StartUs,
    long EndUs,
    TStart StartData,
    TStop StopData) where TKey : notnull
{
    public long FullDurationUs => checked(EndUs - StartUs);
}

internal sealed record IntervalPairResult<TKey, TStart, TStop>(
    IReadOnlyList<PairedInterval<TKey, TStart, TStop>> Pairs,
    IReadOnlyList<PendingIntervalStart<TKey, TStart>> UnmatchedStarts,
    IReadOnlyList<UnmatchedIntervalStop<TKey, TStop>> UnmatchedStops,
    IReadOnlyList<InvalidPairedInterval<TKey, TStart, TStop>> InvalidIntervals)
    where TKey : notnull;

internal sealed class IntervalPairAccumulator<TKey, TStart, TStop> where TKey : notnull
{
    private readonly Dictionary<TKey, Queue<PendingIntervalStart<TKey, TStart>>> _starts = new();
    private readonly List<PairedInterval<TKey, TStart, TStop>> _pairs = new();
    private readonly List<PendingIntervalStart<TKey, TStart>> _unmatchedStarts = new();
    private readonly List<UnmatchedIntervalStop<TKey, TStop>> _unmatchedStops = new();
    private readonly List<InvalidPairedInterval<TKey, TStart, TStop>> _invalidIntervals = new();
    private IntervalPairResult<TKey, TStart, TStop>? _completedResult;

    public void AddStart(TKey key, long timeUs, TStart data)
    {
        ThrowIfCompleted();

        if (!_starts.TryGetValue(key, out var queue))
            _starts[key] = queue = new Queue<PendingIntervalStart<TKey, TStart>>();
        queue.Enqueue(new PendingIntervalStart<TKey, TStart>(key, timeUs, data));
    }

    public void AddStop(TKey key, long timeUs, TStop data)
    {
        ThrowIfCompleted();

        if (!_starts.TryGetValue(key, out var queue) || queue.Count == 0)
        {
            _unmatchedStops.Add(new UnmatchedIntervalStop<TKey, TStop>(key, timeUs, data));
            return;
        }

        var start = queue.Dequeue();
        if (queue.Count == 0)
            _starts.Remove(key);

        if (timeUs <= start.TimeUs)
        {
            _invalidIntervals.Add(new InvalidPairedInterval<TKey, TStart, TStop>(
                key, start.TimeUs, timeUs, start.Data, data));
            return;
        }

        _pairs.Add(new PairedInterval<TKey, TStart, TStop>(
            key, start.TimeUs, timeUs, start.Data, data));
    }

    public IntervalPairResult<TKey, TStart, TStop> Complete()
    {
        if (_completedResult is not null)
            return _completedResult;

        foreach (var queue in _starts.Values)
        {
            while (queue.Count > 0)
                _unmatchedStarts.Add(queue.Dequeue());
        }
        _starts.Clear();

        _completedResult = new IntervalPairResult<TKey, TStart, TStop>(
            _pairs,
            _unmatchedStarts,
            _unmatchedStops,
            _invalidIntervals);
        return _completedResult;
    }

    private void ThrowIfCompleted()
    {
        if (_completedResult is not null)
            throw new InvalidOperationException("Cannot add interval events after completion.");
    }
}
