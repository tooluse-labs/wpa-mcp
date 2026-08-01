using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Analyzers;
using WpaMcp.Output;

namespace WpaMcp.Core;

public sealed class TraceCache : IDisposable
{
    private readonly LruCache<string, CacheEntry> _cache;
    private readonly Func<string, TraceLog> _openTrace;
    private readonly Action<TraceLog> _disposeTrace;
    private int _disposed;

    public TraceCache(int capacity = 0)
        : this(capacity, static path => TraceLog.OpenOrConvert(path), static trace => trace.Dispose())
    {
    }

    internal TraceCache(
        int capacity,
        Func<string, TraceLog> openTrace,
        Action<TraceLog> disposeTrace)
    {
        ArgumentNullException.ThrowIfNull(openTrace);
        ArgumentNullException.ThrowIfNull(disposeTrace);
        if (capacity == 0)
        {
            var env = Environment.GetEnvironmentVariable("WPAMCP_CACHE_SIZE");
            if (!int.TryParse(env, out capacity) || capacity <= 0) capacity = 2;
        }

        _openTrace = openTrace;
        _disposeTrace = disposeTrace;
        _cache = new LruCache<string, CacheEntry>(capacity, static entry => entry.Retire());
    }

    /// <summary>
    /// Acquires a query-scoped lease. The lease must remain alive for every use of its
    /// trace, capabilities, and metadata so eviction cannot dispose an in-flight query.
    /// </summary>
    public TraceLease Acquire(string path)
    {
        ThrowIfDisposed();
        if (!File.Exists(path))
            throw new FileNotFoundException($"trace file not found: {path}", path);

        var canonical = Path.GetFullPath(path);
        CacheEntry? insertedByThisCall = null;

        while (true)
        {
            ThrowIfDisposed();
            if (!File.Exists(canonical))
                throw new FileNotFoundException($"trace file not found: {path}", path);
            var mtime = File.GetLastWriteTimeUtc(canonical);
            CacheEntry? observed = null;

            if (_cache.TryGetAndPin(canonical, entry =>
                {
                    observed = entry;
                    return Volatile.Read(ref _disposed) == 0
                           && entry.MTimeUtc == mtime
                           && entry.TryAcquire();
                }, out var entry))
            {
                var lease = new TraceLease(entry);
                try
                {
                    // Materialize while leased. ExecutionAndPublication keeps concurrent
                    // queries on one open, and a failed open is invalidated below.
                    _ = lease.Trace;
                    if (File.GetLastWriteTimeUtc(canonical) != entry.MTimeUtc)
                    {
                        RetireIfCurrent(canonical, entry);
                        lease.Dispose();
                        insertedByThisCall = null;
                        continue;
                    }
                }
                catch
                {
                    RetireIfCurrent(canonical, entry);
                    lease.Dispose();
                    throw;
                }

                if (ReferenceEquals(insertedByThisCall, entry))
                    TraceCacheCallContext.RecordMiss();
                else
                    TraceCacheCallContext.RecordHit();
                return lease;
            }

            if (observed is not null)
                RetireIfCurrent(canonical, observed);

            ThrowIfDisposed();
            var candidate = new CacheEntry(
                mtime,
                canonical,
                _openTrace,
                _disposeTrace);
            try
            {
                var winner = _cache.GetOrAdd(canonical, _ => candidate, out var added);
                insertedByThisCall = added && ReferenceEquals(winner, candidate)
                    ? candidate
                    : null;
            }
            catch (ObjectDisposedException)
            {
                throw new ObjectDisposedException(nameof(TraceCache));
            }
        }
    }

    /// <summary>
    /// Transitional compatibility for analyzer tests. The returned trace has no
    /// cross-call lease protection and may be disposed after eviction or unload.
    /// Production queries must use <see cref="Acquire"/> instead.
    /// </summary>
    internal TraceLog Get(string path)
    {
        using var lease = Acquire(path);
        return lease.Trace;
    }

    internal TraceCapabilities GetCapabilities(string path)
    {
        using var lease = Acquire(path);
        return lease.Capabilities;
    }

    internal TraceMetadata GetMetadata(string path)
    {
        using var lease = Acquire(path);
        return lease.Metadata;
    }

    public bool Unload(string path)
    {
        ThrowIfDisposed();
        var canonical = Path.GetFullPath(path);
        try
        {
            return _cache.Remove(canonical);
        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(nameof(TraceCache));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cache.Dispose();
    }

    private void RetireIfCurrent(string canonical, CacheEntry entry)
    {
        try
        {
            _cache.Remove(canonical, candidate => ReferenceEquals(candidate, entry));
        }
        catch (ObjectDisposedException)
        {
            // Cache disposal already retired every resident entry.
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(TraceCache));
    }

    internal sealed class CacheEntry
    {
        private readonly object _lock = new();
        private readonly Action<TraceLog> _disposeTrace;
        private int _leaseCount;
        private bool _retired;
        private bool _disposeClaimed;

        internal CacheEntry(
            DateTime mTimeUtc,
            string canonicalPath,
            Func<string, TraceLog> openTrace,
            Action<TraceLog> disposeTrace)
        {
            MTimeUtc = mTimeUtc;
            _disposeTrace = disposeTrace;
            Trace = new Lazy<TraceLog>(() =>
            {
                TraceLog? opened = null;
                try
                {
                    opened = openTrace(canonicalPath);
                    TraceSymbolContext.Register(opened, canonicalPath);
                    return opened;
                }
                catch
                {
                    if (opened is not null)
                        disposeTrace(opened);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication);
            Capabilities = new Lazy<TraceCapabilities>(
                () => TraceCapabilitiesDetector.Detect(Trace.Value),
                LazyThreadSafetyMode.ExecutionAndPublication);
            Metadata = new Lazy<TraceMetadata>(
                () => TraceMetadataAnalysis.Analyze(Trace.Value, Capabilities.Value),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal DateTime MTimeUtc { get; }
        internal Lazy<TraceLog> Trace { get; }
        internal Lazy<TraceCapabilities> Capabilities { get; }
        internal Lazy<TraceMetadata> Metadata { get; }

        internal bool TryAcquire()
        {
            lock (_lock)
            {
                if (_retired)
                    return false;
                checked { _leaseCount++; }
                return true;
            }
        }

        internal void Retire()
        {
            TraceLog? traceToDispose;
            lock (_lock)
            {
                if (_retired)
                    return;
                _retired = true;
                traceToDispose = ClaimTraceForDisposal();
            }

            if (traceToDispose is not null)
                _disposeTrace(traceToDispose);
        }

        internal void Release()
        {
            TraceLog? traceToDispose;
            lock (_lock)
            {
                if (_leaseCount <= 0)
                    throw new InvalidOperationException("Trace lease reference count underflow.");
                _leaseCount--;
                traceToDispose = ClaimTraceForDisposal();
            }

            if (traceToDispose is not null)
                _disposeTrace(traceToDispose);
        }

        private TraceLog? ClaimTraceForDisposal()
        {
            if (!_retired || _leaseCount != 0 || _disposeClaimed)
                return null;

            _disposeClaimed = true;
            return Trace.IsValueCreated ? Trace.Value : null;
        }
    }
}

public sealed class TraceLease : IDisposable
{
    private TraceCache.CacheEntry? _entry;

    internal TraceLease(TraceCache.CacheEntry entry) => _entry = entry;

    public TraceLog Trace => GetEntry().Trace.Value;

    public TraceCapabilities Capabilities => GetEntry().Capabilities.Value;

    public TraceMetadata Metadata => GetEntry().Metadata.Value;

    public void Dispose() => Interlocked.Exchange(ref _entry, null)?.Release();

    private TraceCache.CacheEntry GetEntry() =>
        Volatile.Read(ref _entry)
        ?? throw new ObjectDisposedException(nameof(TraceLease));
}
