using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Analyzers;
using WpaMcp.Output;

namespace WpaMcp.Core;

public sealed class TraceCache
{
    private readonly LruCache<string, CacheEntry> _cache;
    private readonly object _lock = new();

    public TraceCache(int capacity = 0)
    {
        if (capacity == 0)
        {
            var env = Environment.GetEnvironmentVariable("WPAMCP_CACHE_SIZE");
            if (!int.TryParse(env, out capacity) || capacity <= 0) capacity = 2;
        }
        _cache = new LruCache<string, CacheEntry>(capacity);
    }

    public TraceLog Get(string path) => GetEntry(path).Trace.Value;

    /// <summary>
    /// Returns the trace's <see cref="TraceCapabilities"/>, computing on first access and
    /// caching for subsequent calls. Avoids re-walking the event source on every LoadTrace.
    /// </summary>
    public TraceCapabilities GetCapabilities(string path) => GetEntry(path).Capabilities.Value;

    public TraceMetadata GetMetadata(string path) => GetEntry(path).Metadata.Value;

    public bool Unload(string path)
    {
        var canonical = Path.GetFullPath(path);
        return _cache.Remove(canonical);
    }

    private CacheEntry GetEntry(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"trace file not found: {path}", path);

        var canonical = Path.GetFullPath(path);
        SymbolPathDefaults.EnsureTraceDirectory(canonical);
        var mtime = File.GetLastWriteTimeUtc(canonical);

        lock (_lock)
        {
            if (_cache.TryGet(canonical, out var existing) && existing.MTimeUtc == mtime)
            {
                TraceCacheCallContext.RecordHit();
                return existing;
            }
            if (_cache.TryGet(canonical, out _))
                _cache.Remove(canonical);
        }

        TraceCacheCallContext.RecordMiss();
        return _cache.GetOrAdd(canonical, p =>
        {
            var trace = new Lazy<TraceLog>(() => TraceLog.OpenOrConvert(p), isThreadSafe: true);
            var caps = new Lazy<TraceCapabilities>(
                () => TraceCapabilitiesDetector.Detect(trace.Value), isThreadSafe: true);
            var metadata = new Lazy<TraceMetadata>(
                () => TraceMetadataAnalysis.Analyze(trace.Value, caps.Value), isThreadSafe: true);
            return new CacheEntry(mtime, trace, caps, metadata);
        });
    }

    private sealed record CacheEntry(
        DateTime MTimeUtc,
        Lazy<TraceLog> Trace,
        Lazy<TraceCapabilities> Capabilities,
        Lazy<TraceMetadata> Metadata);
}
