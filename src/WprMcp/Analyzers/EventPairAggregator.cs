namespace WprMcp.Analyzers;

internal sealed class EventPairAggregator
{
    private readonly Dictionary<string, Queue<EventPairStart>> _starts = new(StringComparer.Ordinal);
    private readonly List<EventPair> _pairs = new();
    private readonly List<EventPairStart> _unmatchedStarts = new();
    private readonly List<EventPairStop> _unmatchedStops = new();
    private bool _completed;

    public void AddStart(string key, long timeUs, IReadOnlyDictionary<string, string> fields)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!_starts.TryGetValue(key, out var queue))
            _starts[key] = queue = new Queue<EventPairStart>();
        queue.Enqueue(new EventPairStart(key, timeUs, fields));
    }

    public void AddStop(string key, long timeUs, IReadOnlyDictionary<string, string> fields)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (_starts.TryGetValue(key, out var queue) && queue.Count > 0)
        {
            var start = queue.Dequeue();
            if (queue.Count == 0)
                _starts.Remove(key);

            _pairs.Add(new EventPair(
                Key: key,
                StartUs: start.TimeUs,
                StopUs: timeUs,
                DurationUs: Math.Max(0, timeUs - start.TimeUs),
                StartFields: start.Fields,
                StopFields: fields));
            return;
        }

        _unmatchedStops.Add(new EventPairStop(key, timeUs, fields));
    }

    public EventPairResult Complete()
    {
        if (!_completed)
        {
            foreach (var queue in _starts.Values)
            {
                while (queue.Count > 0)
                    _unmatchedStarts.Add(queue.Dequeue());
            }

            _starts.Clear();
            _completed = true;
        }

        return new EventPairResult(_pairs, _unmatchedStarts, _unmatchedStops);
    }
}

internal sealed record EventPairStart(
    string Key,
    long TimeUs,
    IReadOnlyDictionary<string, string> Fields);

internal sealed record EventPairStop(
    string Key,
    long TimeUs,
    IReadOnlyDictionary<string, string> Fields);

internal sealed record EventPair(
    string Key,
    long StartUs,
    long StopUs,
    long DurationUs,
    IReadOnlyDictionary<string, string> StartFields,
    IReadOnlyDictionary<string, string> StopFields);

internal sealed record EventPairResult(
    IReadOnlyList<EventPair> Pairs,
    IReadOnlyList<EventPairStart> UnmatchedStarts,
    IReadOnlyList<EventPairStop> UnmatchedStops);
