using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;
using Xunit;

namespace WpaMcp.Tests;

public class TraceCacheTests
{
    private const string FixturePath = "fixtures/small_cpu.etl"; // captured by fixtures/capture_all.ps1

    [Fact]
    public void Acquire_ReturnsSameInstanceAcrossCalls()
    {
        using var cache = new TraceCache(capacity: 2);
        using var first = cache.Acquire(FixturePath);
        using var second = cache.Acquire(FixturePath);

        Assert.Same(first.Trace, second.Trace);
        Assert.Same(first.Capabilities, second.Capabilities);
        Assert.Same(first.Metadata, second.Metadata);
    }

    [Fact]
    public void Acquire_ReloadsAfterMtimeBumpWithoutInvalidatingOldLease()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"wpa-mcp-mtime-{Guid.NewGuid():N}.etl");
        File.Copy(FixturePath, tmp);
        try
        {
            var disposeCount = 0;
            using var cache = TrackingCache(capacity: 2, () => Interlocked.Increment(ref disposeCount));
            using var first = cache.Acquire(tmp);
            var originalTrace = first.Trace;

            // Bump mtime by re-copying.
            File.Copy(FixturePath, tmp, overwrite: true);
            File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow.AddSeconds(5));

            using var second = cache.Acquire(tmp);
            Assert.NotSame(originalTrace, second.Trace);
            Assert.Equal(0, Volatile.Read(ref disposeCount));
            Assert.True(originalTrace.EventCount > 0);

            first.Dispose();
            Assert.Equal(1, Volatile.Read(ref disposeCount));
        }
        finally
        {
            try { File.Delete(tmp); File.Delete(tmp + "x"); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Unload_RetiresEntryAndDefersDisposeUntilLastLease()
    {
        var disposeCount = 0;
        using var cache = TrackingCache(capacity: 2, () => Interlocked.Increment(ref disposeCount));
        var first = cache.Acquire(FixturePath);
        var sibling = cache.Acquire(FixturePath);
        var originalTrace = first.Trace;

        Assert.True(cache.Unload(FixturePath));
        Assert.Equal(0, Volatile.Read(ref disposeCount));
        Assert.True(originalTrace.EventCount > 0);

        using var replacement = cache.Acquire(FixturePath);
        Assert.NotSame(originalTrace, replacement.Trace);

        first.Dispose();
        Assert.Equal(0, Volatile.Read(ref disposeCount));
        Assert.Same(originalTrace, sibling.Trace);

        sibling.Dispose();
        Assert.Equal(1, Volatile.Read(ref disposeCount));
    }

    [Fact]
    public void Acquire_ThrowsForMissingFile()
    {
        using var cache = new TraceCache(capacity: 2);
        Assert.Throws<FileNotFoundException>(() => cache.Acquire("nonexistent.etl"));
    }

    [Fact]
    public void CapacityEviction_DefersDisposeUntilActiveLeaseEnds()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"wpa-mcp-eviction-{Guid.NewGuid():N}.etl");
        File.Copy(FixturePath, tmp);
        try
        {
            var disposeCount = 0;
            using var cache = TrackingCache(capacity: 1, () => Interlocked.Increment(ref disposeCount));
            var first = cache.Acquire(FixturePath);
            var originalTrace = first.Trace;

            using var second = cache.Acquire(tmp); // evicts, but cannot dispose, first
            Assert.Equal(0, Volatile.Read(ref disposeCount));
            Assert.True(originalTrace.EventCount > 0);

            first.Dispose();
            Assert.Equal(1, Volatile.Read(ref disposeCount));
        }
        finally
        {
            try { File.Delete(tmp); File.Delete(tmp + "x"); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Dispose_RetiresEntriesAndRejectsNewAcquire()
    {
        var disposeCount = 0;
        var cache = TrackingCache(capacity: 2, () => Interlocked.Increment(ref disposeCount));
        var lease = cache.Acquire(FixturePath);
        var trace = lease.Trace;

        cache.Dispose();

        Assert.Equal(0, Volatile.Read(ref disposeCount));
        Assert.True(trace.EventCount > 0);
        Assert.Throws<ObjectDisposedException>(() => cache.Acquire(FixturePath));

        lease.Dispose();
        Assert.Equal(1, Volatile.Read(ref disposeCount));
        cache.Dispose();
        Assert.Equal(1, Volatile.Read(ref disposeCount));
    }

    [Fact]
    public void LeaseDispose_IsIdempotentAndRejectsFurtherAccess()
    {
        using var cache = new TraceCache(capacity: 2);
        var lease = cache.Acquire(FixturePath);
        _ = lease.Trace;

        lease.Dispose();
        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = lease.Trace; });
        Assert.Throws<ObjectDisposedException>(() => { _ = lease.Capabilities; });
        Assert.Throws<ObjectDisposedException>(() => { _ = lease.Metadata; });
    }

    [Fact]
    public void RetiringNeverEvaluatedEntry_DoesNotOpenTrace()
    {
        var opened = false;
        var disposed = false;
        var entry = new TraceCache.CacheEntry(
            DateTime.UtcNow,
            "never-opened.etl",
            _ =>
            {
                opened = true;
                throw new InvalidOperationException("must not run");
            },
            _ => disposed = true);

        entry.Retire();
        entry.Retire();

        Assert.False(opened);
        Assert.False(disposed);
    }

    [Fact]
    public void OpenerFailure_DoesNotPermanentlyPoisonCacheEntry()
    {
        var openCount = 0;
        using var cache = new TraceCache(
            capacity: 2,
            openTrace: path =>
            {
                if (Interlocked.Increment(ref openCount) == 1)
                    throw new IOException("transient test failure");
                return TraceLog.OpenOrConvert(path);
            },
            disposeTrace: trace => trace.Dispose());

        Assert.Throws<IOException>(() => cache.Acquire(FixturePath));

        using var lease = cache.Acquire(FixturePath);
        Assert.True(lease.Trace.EventCount > 0);
        Assert.Equal(2, Volatile.Read(ref openCount));
    }

    [Fact]
    public async Task ConcurrentAcquire_UsesOneLazyOpenAndOneTraceInstance()
    {
        var openCount = 0;
        using var openerEntered = new ManualResetEventSlim();
        using var allowOpen = new ManualResetEventSlim();
        using var cache = new TraceCache(
            capacity: 2,
            openTrace: path =>
            {
                Interlocked.Increment(ref openCount);
                openerEntered.Set();
                Assert.True(allowOpen.Wait(TimeSpan.FromSeconds(5)));
                return TraceLog.OpenOrConvert(path);
            },
            disposeTrace: trace => trace.Dispose());

        Task<TraceLog> Acquire() => Task.Run(() =>
        {
            using var lease = cache.Acquire(FixturePath);
            return lease.Trace;
        });

        var queries = Enumerable.Range(0, 6).Select(_ => Acquire()).ToArray();
        Assert.True(openerEntered.Wait(TimeSpan.FromSeconds(5)));
        allowOpen.Set();
        var traces = await Task.WhenAll(queries);

        Assert.Equal(1, Volatile.Read(ref openCount));
        Assert.All(traces, trace => Assert.Same(traces[0], trace));
    }

    private static TraceCache TrackingCache(int capacity, Action onDispose)
        => new(
            capacity,
            path => TraceLog.OpenOrConvert(path),
            trace =>
            {
                onDispose();
                trace.Dispose();
            });
}
