using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class CacheAdvancedTests
{
    private static SearchCache Create(TimeSpan? ttl = null, int maxEntries = 100)
        => new(new SearchCacheOptions { Ttl = ttl ?? TimeSpan.FromMinutes(15), MaxEntries = maxEntries });

    private static SearchResponse Response(string query) => new() { Query = query, CompletedAt = DateTimeOffset.UtcNow };

    [Fact]
    public void Per_entry_ttl_overrides_default()
    {
        var cache = Create(ttl: TimeSpan.FromMinutes(15));
        cache.Set("short", Response("s"), TimeSpan.FromMilliseconds(80));

        Thread.Sleep(200);

        Assert.Null(cache.Get("short"));
    }

    [Fact]
    public void Entries_with_default_ttl_survive_short_override_eviction()
    {
        var cache = Create(ttl: TimeSpan.FromMinutes(15));
        cache.Set("long", Response("l"));
        cache.Set("short", Response("s"), TimeSpan.FromMilliseconds(80));

        Thread.Sleep(200);

        Assert.NotNull(cache.Get("long"));
        Assert.Null(cache.Get("short"));
    }

    [Fact]
    public void Invalidate_where_removes_matching_entries_only()
    {
        var cache = Create();
        cache.Set("alpha|news", Response("a"));
        cache.Set("beta|video", Response("b"));

        cache.InvalidateWhere(k => k.Contains("video", StringComparison.Ordinal));

        Assert.Equal(1, cache.Count);
        Assert.NotNull(cache.Get("alpha|news"));
    }

    [Fact]
    public void Tracks_hits_and_misses()
    {
        var cache = Create();
        cache.Set("k", Response("q"));

        cache.Get("k");
        cache.Get("k");
        cache.Get("missing");

        Assert.Equal(2, cache.Hits);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public void Top_keys_orders_by_hit_count()
    {
        var cache = Create();
        cache.Set("a", Response("1"));
        cache.Set("b", Response("2"));
        cache.Set("c", Response("3"));

        cache.Get("b");
        cache.Get("b");
        cache.Get("c");

        var top = cache.TopKeys(2);

        Assert.Equal(new[] { "b", "c" }, top);
    }

    [Fact]
    public void Per_type_ttl_map_is_available_in_options()
    {
        var options = new SearchCacheOptions();

        Assert.Contains(SearchResultType.News, options.PerTypeTtl.Keys);
        Assert.Contains(SearchResultType.Local, options.PerTypeTtl.Keys);
        Assert.True(options.PerTypeTtl[SearchResultType.News] < options.PerTypeTtl[SearchResultType.Local]);
    }

    [Fact]
    public void Expired_entries_do_not_count()
    {
        var cache = Create(ttl: TimeSpan.FromMilliseconds(80), maxEntries: 5);
        cache.Set("k", Response("q"));

        Thread.Sleep(200);

        Assert.Equal(0, cache.Count);
    }
}
