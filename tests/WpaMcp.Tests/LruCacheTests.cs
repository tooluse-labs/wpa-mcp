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
}
