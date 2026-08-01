using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Analyzers;
using WpaMcp.Output;

namespace WpaMcp.Core;

public sealed class TraceCache : IDisposable
{
    private readonly LruCache<string, CacheEntry> _cache;
    private readonly Func<string, TraceLog> _openTrace;
    private readonly Action<TraceLog> _disposeTrace;
    private readonly bool _refreshStaleSidecars;
    private readonly HashSet<string> _sidecarRefreshRequests;
    private readonly object _sidecarRefreshLock = new();
    private readonly HashSet<CacheEntry> _retiredEntries = [];
    private readonly HashSet<CacheEntry> _lruOwnedRetirements = [];
    private readonly object _retiredEntriesLock = new();
    private int _disposed;

    public TraceCache(int capacity = 0)
        : this(
            capacity,
            static path => TraceLog.OpenOrConvert(path),
            static trace => trace.Dispose(),
            refreshStaleSidecars: true)
    {
    }

    internal TraceCache(
        int capacity,
        Func<string, TraceLog> openTrace,
        Action<TraceLog> disposeTrace,
        bool refreshStaleSidecars = false)
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
        _refreshStaleSidecars = refreshStaleSidecars;
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        _sidecarRefreshRequests = new HashSet<string>(pathComparer);
        _cache = new LruCache<string, CacheEntry>(
            capacity,
            RetireEntry,
            pathComparer);
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
            var stamp = FileStamp.Capture(canonical);
            CacheEntry? observed = null;

            if (_cache.TryGetAndPin(canonical, entry =>
                {
                    observed = entry;
                    return Volatile.Read(ref _disposed) == 0
                           && entry.Stamp == stamp
                           && entry.TryAcquire();
                }, out var entry))
            {
                var lease = new TraceLease(entry);
                try
                {
                    // Materialize while leased. ExecutionAndPublication keeps concurrent
                    // queries on one open, and a failed open is invalidated below.
                    _ = lease.Trace;
                    if (FileStamp.Capture(canonical) != entry.Stamp)
                    {
                        RequestSidecarRefresh(canonical);
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
            {
                if (observed.Stamp != stamp)
                    RequestSidecarRefresh(canonical);
                RetireIfCurrent(canonical, observed);
            }

            ThrowIfDisposed();
            var candidate = new CacheEntry(
                stamp,
                canonical,
                OpenTrace,
                _disposeTrace,
                OnEntryRetirementCompleted);
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
        // Unload is also the explicit freshness escape hatch for an in-place rewrite
        // whose identity, length, and timestamps were deliberately preserved.
        RequestSidecarRefresh(canonical);
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
        Interlocked.Exchange(ref _disposed, 1);
        var failures = new List<Exception>();

        // Entries retired while leased are no longer resident in the LRU. Keep their
        // cleanup centrally retryable even after the final TraceLease has gone away.
        CacheEntry[] pending;
        lock (_retiredEntriesLock)
            pending = [.. _retiredEntries.Where(entry => !_lruOwnedRetirements.Contains(entry))];
        foreach (var entry in pending)
        {
            try
            {
                entry.Retire();
            }
            catch (Exception ex)
            {
                AddDisposeFailures(failures, ex);
            }
        }

        // LruCache independently retains failed removal callbacks. Calling it after
        // the central retry lets either owner complete cleanup without double-dispose:
        // CacheEntry serializes and remembers successful retirement.
        try
        {
            _cache.Dispose();
        }
        catch (Exception ex)
        {
            AddDisposeFailures(failures, ex);
        }

        if (failures.Count != 0)
            throw new AggregateException("One or more trace cache entries failed to dispose.", failures);
    }

    private void RetireEntry(CacheEntry entry)
    {
        lock (_retiredEntriesLock)
            _retiredEntries.Add(entry);
        try
        {
            entry.Retire();
        }
        catch
        {
            // LruCache retains callbacks that throw. Mark that retry owner so the
            // central pass does not make a duplicate disposal attempt in the same
            // Dispose call before the LRU can aggregate/retry its callback.
            lock (_retiredEntriesLock)
                _lruOwnedRetirements.Add(entry);
            throw;
        }
    }

    private void OnEntryRetirementCompleted(CacheEntry entry)
    {
        lock (_retiredEntriesLock)
        {
            _retiredEntries.Remove(entry);
            _lruOwnedRetirements.Remove(entry);
        }
    }

    private static void AddDisposeFailures(List<Exception> failures, Exception failure)
    {
        if (failure is AggregateException aggregate)
            failures.AddRange(aggregate.Flatten().InnerExceptions);
        else
            failures.Add(failure);
    }

    private TraceLog OpenTrace(string canonical)
    {
        var refresh = ConsumeSidecarRefresh(canonical);
        try
        {
            if (refresh)
                MakeDerivedEtlxOlderThanSource(canonical);
            return _openTrace(canonical);
        }
        catch
        {
            if (refresh)
                RequestSidecarRefresh(canonical);
            throw;
        }
    }

    private void RequestSidecarRefresh(string canonical)
    {
        if (!_refreshStaleSidecars ||
            !Path.GetExtension(canonical).Equals(".etl", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_sidecarRefreshLock)
            _sidecarRefreshRequests.Add(canonical);
    }

    private bool ConsumeSidecarRefresh(string canonical)
    {
        if (!_refreshStaleSidecars)
            return false;
        lock (_sidecarRefreshLock)
            return _sidecarRefreshRequests.Remove(canonical);
    }

    private static void MakeDerivedEtlxOlderThanSource(string etlPath)
    {
        var etlxPath = Path.ChangeExtension(etlPath, ".etlx");
        if (!File.Exists(etlxPath))
            return;

        var sourceWrite = File.GetLastWriteTimeUtc(etlPath);
        var staleWrite = sourceWrite > DateTime.MinValue.AddSeconds(2)
            ? sourceWrite.AddSeconds(-2)
            : DateTime.MinValue;
        if (File.GetLastWriteTimeUtc(etlxPath) >= sourceWrite)
            File.SetLastWriteTimeUtc(etlxPath, staleWrite);
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
        private readonly Action<CacheEntry>? _onRetirementCompleted;
        private int _leaseCount;
        private bool _retired;
        private bool _disposeClaimed;
        private bool _disposed;

        internal CacheEntry(
            FileStamp stamp,
            string canonicalPath,
            Func<string, TraceLog> openTrace,
            Action<TraceLog> disposeTrace,
            Action<CacheEntry>? onRetirementCompleted = null)
        {
            Stamp = stamp;
            _disposeTrace = disposeTrace;
            _onRetirementCompleted = onRetirementCompleted;
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

        internal FileStamp Stamp { get; }
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
            bool retirementCompleted;
            lock (_lock)
            {
                _retired = true;
                traceToDispose = ClaimTraceForDisposal(out retirementCompleted);
            }

            if (traceToDispose is not null)
                DisposeClaimedTrace(traceToDispose);
            else if (retirementCompleted)
                _onRetirementCompleted?.Invoke(this);
        }

        internal void Release()
        {
            TraceLog? traceToDispose;
            bool retirementCompleted;
            lock (_lock)
            {
                if (_leaseCount <= 0)
                    throw new InvalidOperationException("Trace lease reference count underflow.");
                _leaseCount--;
                traceToDispose = ClaimTraceForDisposal(out retirementCompleted);
            }

            if (traceToDispose is not null)
                DisposeClaimedTrace(traceToDispose);
            else if (retirementCompleted)
                _onRetirementCompleted?.Invoke(this);
        }

        private TraceLog? ClaimTraceForDisposal(out bool retirementCompleted)
        {
            retirementCompleted = _disposed;
            if (_disposed || !_retired || _leaseCount != 0 || _disposeClaimed)
                return null;

            if (!Trace.IsValueCreated)
            {
                _disposed = true;
                retirementCompleted = true;
                return null;
            }

            _disposeClaimed = true;
            return Trace.Value;
        }

        private void DisposeClaimedTrace(TraceLog trace)
        {
            try
            {
                _disposeTrace(trace);
            }
            catch
            {
                lock (_lock)
                    _disposeClaimed = false;
                throw;
            }

            lock (_lock)
            {
                _disposeClaimed = false;
                _disposed = true;
            }
            _onRetirementCompleted?.Invoke(this);
        }
    }

    internal readonly record struct FileStamp(
        string CanonicalPath,
        DateTime LastWriteTimeUtc,
        DateTime CreationTimeUtc,
        long Length,
        uint? VolumeSerialNumber,
        ulong? FileId)
    {
        internal static FileStamp Capture(string path)
        {
            var canonical = Path.GetFullPath(path);
            var comparablePath = OperatingSystem.IsWindows()
                ? canonical.ToUpperInvariant()
                : canonical;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using SafeFileHandle handle = File.OpenHandle(
                        canonical,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    if (GetFileInformationByHandle(handle, out var native))
                    {
                        return new FileStamp(
                            comparablePath,
                            FromFileTimeUtc(native.LastWriteTime),
                            FromFileTimeUtc(native.CreationTime),
                            ((long)native.FileSizeHigh << 32) | native.FileSizeLow,
                            native.VolumeSerialNumber,
                            ((ulong)native.FileIndexHigh << 32) | native.FileIndexLow);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Fall back to portable metadata when this file system does not expose
                    // a stable identity to the process.
                }
            }

            var info = new FileInfo(canonical);
            info.Refresh();
            if (!info.Exists)
                throw new FileNotFoundException($"trace file not found: {path}", path);

            return new FileStamp(
                comparablePath,
                info.LastWriteTimeUtc,
                info.CreationTimeUtc,
                info.Length,
                VolumeSerialNumber: null,
                FileId: null);
        }

        private static DateTime FromFileTimeUtc(
            System.Runtime.InteropServices.ComTypes.FILETIME value)
        {
            var raw = ((long)(uint)value.dwHighDateTime << 32) |
                      (uint)value.dwLowDateTime;
            return DateTime.FromFileTimeUtc(raw);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }
    }
}

public sealed class TraceLease : IDisposable
{
    private readonly object _disposeLock = new();
    private TraceCache.CacheEntry? _entry;

    internal TraceLease(TraceCache.CacheEntry entry) => _entry = entry;

    public TraceLog Trace => GetEntry().Trace.Value;

    public TraceCapabilities Capabilities => GetEntry().Capabilities.Value;

    public TraceMetadata Metadata => GetEntry().Metadata.Value;

    public void Dispose()
    {
        lock (_disposeLock)
        {
            var entry = _entry;
            if (entry is null)
                return;

            // Relinquish the lease exactly once even if native cleanup fails. Retired
            // entry cleanup is owned and retried by TraceCache, not by a local using.
            _entry = null;
            entry.Release();
        }
    }

    private TraceCache.CacheEntry GetEntry() =>
        Volatile.Read(ref _entry)
        ?? throw new ObjectDisposedException(nameof(TraceLease));
}
