using WpaMcp.Core;
using Xunit;

namespace WpaMcp.Tests;

public class LruCacheTests
{
    [Fact]
    public void GetOrAdd_AddsAndReturnsValue()
    {
        var cache = new LruCache<string, int>(capacity: 2);
        var v = cache.GetOrAdd("a", _ => 1);
        Assert.Equal(1, v);
        Assert.True(cache.TryGet("a", out var stored));
        Assert.Equal(1, stored);
    }

    [Fact]
    public void GetOrAdd_RecentEvictsLeastRecent_AtCapacity()
    {
        var cache = new LruCache<string, int>(capacity: 2);
        cache.GetOrAdd("a", _ => 1);
        cache.GetOrAdd("b", _ => 2);
        cache.GetOrAdd("c", _ => 3); // should evict "a"
        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void TryGet_PromotesToMostRecent()
    {
        var cache = new LruCache<string, int>(capacity: 2);
        cache.GetOrAdd("a", _ => 1);
        cache.GetOrAdd("b", _ => 2);
        cache.TryGet("a", out _);            // a now MRU
        cache.GetOrAdd("c", _ => 3);          // should evict "b", not "a"
        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void Remove_DropsEntry()
    {
        var cache = new LruCache<string, int>(capacity: 2);
        cache.GetOrAdd("a", _ => 1);
        Assert.True(cache.Remove("a"));
        Assert.False(cache.TryGet("a", out _));
    }

    [Fact]
    public void Constructor_RejectsZeroCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LruCache<string, int>(0));
    }

    [Fact]
    public void RemovalCallback_ObservesEvictionRemoveAndDisposeExactlyOnce()
    {
        var removed = new List<int>();
        var cache = new LruCache<string, int>(capacity: 2, removed.Add);

        cache.GetOrAdd("a", _ => 1);
        cache.GetOrAdd("b", _ => 2);
        cache.GetOrAdd("c", _ => 3); // evicts a
        Assert.True(cache.Remove("b"));
        cache.Dispose(); // retires c

        Assert.Equal([1, 2, 3], removed);
    }

    [Fact]
    public void RemovalCallback_RunsOutsideInternalLock()
    {
        using var callbackAccessedCache = new ManualResetEventSlim();
        LruCache<string, int>? cache = null;
        cache = new LruCache<string, int>(capacity: 1, removed =>
        {
            if (removed != 1)
                return;
            ThreadPool.QueueUserWorkItem(state =>
            {
                cache!.TryGet("b", out _);
                callbackAccessedCache.Set();
            });
            Assert.True(callbackAccessedCache.Wait(TimeSpan.FromSeconds(5)));
        });

        cache.GetOrAdd("a", _ => 1);
        cache.GetOrAdd("b", _ => 2);
    }

    [Fact]
    public async Task GetOrAdd_ConcurrentLoserIsReportedWithoutReplacingWinner()
    {
        var removed = new List<int>();
        var callbackLock = new object();
        using var bothFactoriesReady = new Barrier(participantCount: 2);
        using var cache = new LruCache<string, int>(capacity: 2, value =>
        {
            lock (callbackLock)
                removed.Add(value);
        });
        var next = 0;

        Task<int> Add() => Task.Run(() => cache.GetOrAdd("same", _ =>
        {
            var value = Interlocked.Increment(ref next);
            Assert.True(bothFactoriesReady.SignalAndWait(TimeSpan.FromSeconds(5)));
            return value;
        }));

        var first = Add();
        var second = Add();
        var values = await Task.WhenAll(first, second);

        Assert.Equal(values[0], values[1]);
        lock (callbackLock)
        {
            Assert.Single(removed);
            Assert.NotEqual(values[0], removed[0]);
        }
    }

    [Fact]
    public async Task TryGetAndPin_LinearizesBeforeConcurrentRemove()
    {
        var removed = new List<int>();
        using var pinEntered = new ManualResetEventSlim();
        using var allowPin = new ManualResetEventSlim();
        using var cache = new LruCache<string, int>(capacity: 1, removed.Add);
        cache.GetOrAdd("a", _ => 1);

        var pin = Task.Run(() => cache.TryGetAndPin("a", value =>
        {
            pinEntered.Set();
            Assert.True(allowPin.Wait(TimeSpan.FromSeconds(5)));
            return value == 1;
        }, out var value) ? value : -1);

        Assert.True(pinEntered.Wait(TimeSpan.FromSeconds(5)));
        var remove = Task.Run(() => cache.Remove("a"));
        await Task.Delay(100);
        Assert.False(remove.IsCompleted);

        allowPin.Set();
        Assert.Equal(1, await pin);
        Assert.True(await remove);
        Assert.Equal([1], removed);
    }

    [Fact]
    public void Dispose_RejectsNewOperations()
    {
        var cache = new LruCache<string, int>(capacity: 1);
        cache.Dispose();

        Assert.Throws<ObjectDisposedException>(() => cache.GetOrAdd("a", _ => 1));
        Assert.Throws<ObjectDisposedException>(() => cache.TryGet("a", out _));
    }

    [Fact]
    public void Dispose_AttemptsEveryRemovalCallbackBeforeReportingFailures()
    {
        var attempted = new List<int>();
        var attemptsByValue = new Dictionary<int, int>();
        var cache = new LruCache<string, int>(capacity: 3, value =>
        {
            attempted.Add(value);
            attemptsByValue[value] = attemptsByValue.GetValueOrDefault(value) + 1;
            if (value is 1 or 3 && attemptsByValue[value] == 1)
                throw new IOException($"failure {value}");
        });
        cache.GetOrAdd("a", _ => 1);
        cache.GetOrAdd("b", _ => 2);
        cache.GetOrAdd("c", _ => 3);

        var error = Assert.Throws<AggregateException>(() => cache.Dispose());

        Assert.Equal([3, 2, 1], attempted);
        Assert.Equal(2, error.InnerExceptions.Count);

        cache.Dispose();

        Assert.Equal([3, 2, 1, 3, 1], attempted);
    }

    [Fact]
    public void FailedEvictionCallback_IsRetriedByDispose()
    {
        var attempts = 0;
        var cache = new LruCache<string, int>(capacity: 1, _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new IOException("transient eviction cleanup failure");
        });
        cache.GetOrAdd("a", _ => 1);

        Assert.Throws<IOException>(() => cache.GetOrAdd("b", _ => 2));
        cache.Dispose();

        Assert.Equal(3, attempts); // retry evicted a, then retire resident b
    }

    [Fact]
    public void ConstructorComparerControlsKeyIdentity()
    {
        using var cache = new LruCache<string, int>(
            capacity: 2,
            comparer: StringComparer.OrdinalIgnoreCase);

        var first = cache.GetOrAdd("Trace.etl", _ => 1);
        var second = cache.GetOrAdd("trace.etl", _ => 2);

        Assert.Equal(first, second);
    }
}
