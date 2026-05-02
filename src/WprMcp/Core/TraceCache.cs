using Microsoft.Diagnostics.Tracing.Etlx;

namespace WprMcp.Core;

public sealed class TraceCache
{
    private readonly LruCache<string, CacheEntry> _cache;
    private readonly object _lock = new();

    public TraceCache(int capacity = 0)
    {
        if (capacity == 0)
        {
            var env = Environment.GetEnvironmentVariable("WPRMCP_CACHE_SIZE");
            if (!int.TryParse(env, out capacity) || capacity <= 0) capacity = 2;
        }
        _cache = new LruCache<string, CacheEntry>(capacity);
    }

    public TraceLog Get(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"trace file not found: {path}", path);

        var canonical = Path.GetFullPath(path);
        var mtime = File.GetLastWriteTimeUtc(canonical);

        lock (_lock)
        {
            if (_cache.TryGet(canonical, out var existing) && existing.MTimeUtc == mtime)
                return existing.Trace.Value;
            if (_cache.TryGet(canonical, out _))
                _cache.Remove(canonical);
        }

        var entry = _cache.GetOrAdd(canonical, p => new CacheEntry(
            mtime,
            new Lazy<TraceLog>(() => TraceLog.OpenOrConvert(p), isThreadSafe: true)));
        return entry.Trace.Value;
    }

    public bool Unload(string path)
    {
        var canonical = Path.GetFullPath(path);
        return _cache.Remove(canonical);
    }

    private sealed record CacheEntry(DateTime MTimeUtc, Lazy<TraceLog> Trace);
}
