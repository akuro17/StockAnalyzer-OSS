#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class BoundedLruCacheTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Constructor_RejectsACapacityBelowOne(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedLruCache<string, int>(capacity));

    [Fact]
    public void Add_BeyondTheCapacity_EvictsTheLeastRecentlyUsedEntry()
    {
        var cache = new BoundedLruCache<string, int>(3);
        foreach (var (key, value) in new[] { ("a", 1), ("b", 2), ("c", 3) }) cache.TryAdd(key, value);
        cache.TryAdd("d", 4);

        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out var b));
        Assert.Equal(2, b);
        Assert.True(cache.TryGet("c", out _));
        Assert.True(cache.TryGet("d", out _));
    }

    [Fact]
    public void Reading_AnEntry_MakesItTheMostRecent()
    {
        var cache = new BoundedLruCache<string, int>(2);
        cache.TryAdd("a", 1);
        cache.TryAdd("b", 2);
        Assert.True(cache.TryGet("a", out _)); // b is now the oldest
        cache.TryAdd("c", 3);

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void Adding_AnExistingKey_KeepsTheFirstValue_AndRefreshesItsRecency()
    {
        var cache = new BoundedLruCache<string, string>(2);
        cache.TryAdd("a", "first");
        cache.TryAdd("b", "other");
        cache.TryAdd("a", "second"); // a becomes the most recent, the value is unchanged
        cache.TryAdd("c", "third");   // evicts b

        Assert.True(cache.TryGet("a", out var a));
        Assert.Equal("first", a);
        Assert.False(cache.TryGet("b", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void CapacityOfOne_HoldsOnlyTheLatestEntry()
    {
        var cache = new BoundedLruCache<int, int>(1);
        cache.TryAdd(1, 10);
        cache.TryAdd(2, 20);

        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(2, out var value));
        Assert.Equal(20, value);
    }

    [Fact]
    public async Task ConcurrentUse_NeverExceedsTheCapacity_AndKeepsTheIndexConsistent()
    {
        const int capacity = 16;
        var cache = new BoundedLruCache<int, int>(capacity);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                var key = (worker * 31 + i) % 64;
                cache.TryAdd(key, key * 2);
                if (cache.TryGet((key + 5) % 64, out var value)) Assert.Equal(((key + 5) % 64) * 2, value);
                Assert.InRange(cache.Count, 1, capacity);
            }
        })));

        Assert.InRange(cache.Count, 1, capacity);
        var present = Enumerable.Range(0, 64).Count(key => cache.TryGet(key, out _));
        Assert.Equal(cache.Count, present);
    }
}
