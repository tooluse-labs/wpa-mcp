namespace WpaMcp.Core;

internal static class TraceCacheCallContext
{
    private static readonly AsyncLocal<TraceCacheCallStats?> Current = new();

    public static IDisposable Begin()
    {
        var previous = Current.Value;
        Current.Value = new TraceCacheCallStats();
        return new Scope(previous);
    }

    public static TraceCacheCallSnapshot Snapshot
        => Current.Value?.Snapshot() ?? TraceCacheCallSnapshot.Empty;

    public static void RecordHit() => Current.Value?.RecordHit();

    public static void RecordMiss() => Current.Value?.RecordMiss();

    private sealed class Scope(TraceCacheCallStats? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            Current.Value = previous;
            _disposed = true;
        }
    }

    private sealed class TraceCacheCallStats
    {
        private int _hits;
        private int _misses;

        public void RecordHit() => _hits++;

        public void RecordMiss() => _misses++;

        public TraceCacheCallSnapshot Snapshot() => new(_hits, _misses);
    }
}

internal sealed record TraceCacheCallSnapshot(int Hits, int Misses)
{
    public static TraceCacheCallSnapshot Empty { get; } = new(0, 0);

    public bool? CacheHit => (Hits, Misses) switch
    {
        (0, 0) => null,
        (> 0, 0) => true,
        _ => false,
    };
}
