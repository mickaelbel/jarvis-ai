using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class SearchCacheTests
{
    private static SearchCache Create(TimeSpan? ttl = null, int maxEntries = 100)
        => new(new SearchCacheOptions { Ttl = ttl ?? TimeSpan.FromMinutes(15), MaxEntries = maxEntries });

    private static SearchResponse Response(string query) => new() { Query = query, CompletedAt = DateTimeOffset.UtcNow };

    [Fact]
    public void Stores_and_retrieves_response()
    {
        var cache = Create();
        cache.Set("k", Response("q"));

        var result = cache.Get("k");

        Assert.NotNull(result);
        Assert.Equal("q", result!.Query);
    }

    [Fact]
    public void Returns_null_for_missing_key()
    {
        var cache = Create();

        Assert.Null(cache.Get("missing"));
    }

    [Fact]
    public void Overwrites_existing_key()
    {
        var cache = Create();
        cache.Set("k", Response("one"));
        cache.Set("k", Response("two"));

        Assert.Equal("two", cache.Get("k")!.Query);
    }

    [Fact]
    public void Invalidate_clears_all_entries()
    {
        var cache = Create();
        cache.Set("k1", Response("a"));
        cache.Set("k2", Response("b"));

        cache.Invalidate();

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Expired_entries_are_evicted()
    {
        var cache = Create(ttl: TimeSpan.FromMilliseconds(100));
        cache.Set("k", Response("q"));

        Thread.Sleep(300);

        Assert.Null(cache.Get("k"));
    }

    [Fact]
    public void Count_reflects_entries()
    {
        var cache = Create();
        cache.Set("a", Response("1"));
        cache.Set("b", Response("2"));

        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Max_entries_evicts_oldest()
    {
        var cache = Create(maxEntries: 2);
        cache.Set("a", Response("1"));
        cache.Set("b", Response("2"));
        cache.Set("c", Response("3"));

        Assert.Equal(2, cache.Count);
        Assert.Null(cache.Get("a"));
        Assert.NotNull(cache.Get("b"));
        Assert.NotNull(cache.Get("c"));
    }

    [Fact]
    public void Recently_accessed_entries_survive_eviction()
    {
        var cache = Create(maxEntries: 2);
        cache.Set("a", Response("1"));
        cache.Set("b", Response("2"));
        cache.Get("a");
        cache.Set("c", Response("3"));

        Assert.NotNull(cache.Get("a"));
        Assert.Null(cache.Get("b"));
    }
}
