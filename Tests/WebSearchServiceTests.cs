using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class WebSearchServiceTests
{
    private static WebSearchService CreateService(
        IReadOnlyList<IWebSearchProvider>? providers = null,
        FakeLinkVerifier? verifier = null,
        ISearchCache? cache = null)
    {
        var providersList = providers ?? new IWebSearchProvider[] { };
        return new WebSearchService(
            providersList,
            verifier ?? new FakeLinkVerifier(),
            cache ?? new SearchCache(),
            new OfficialSiteDetector(),
            new FakeSiteDetector(),
            NullLogger<WebSearchService>.Instance);
    }

    private static StubSearchProvider Provider(string name, params SearchResult[] results)
        => new(name, results);

    [Fact]
    public async Task Aggregates_results_from_multiple_providers()
    {
        var p1 = Provider("duckduckgo", TestResults.Result("A", "https://sample-news.example.org/a", "duckduckgo"));
        var p2 = Provider("bing", TestResults.Result("B", "https://sample-blog.example.org/b", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p1, p2 });

        var response = await service.SearchAsync(new SearchRequest { Query = "test" });

        Assert.Equal(2, response.Results.Count);
        Assert.Contains(response.ProvidersUsed, p => p == "duckduckgo");
        Assert.Contains(response.ProvidersUsed, p => p == "bing");
    }

    [Fact]
    public async Task Dedupes_identical_results_across_providers()
    {
        var p1 = Provider("duckduckgo", TestResults.Result("Same", "https://x.com/art", "duckduckgo"));
        var p2 = Provider("bing", TestResults.Result("Same", "https://x.com/art", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p1, p2 });

        var response = await service.SearchAsync(new SearchRequest { Query = "test" });

        Assert.Single(response.Results);
    }

    [Fact]
    public async Task Removes_failed_links_after_verification()
    {
        var p1 = Provider("bing", TestResults.Result("Dead", "https://dead.example.com", "bing"));
        var verifier = new FakeLinkVerifier();
        verifier.Rejected.Add("https://dead.example.com");
        var service = CreateService(new IWebSearchProvider[] { p1 }, verifier);

        var response = await service.SearchAsync(new SearchRequest { Query = "test" });

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Flags_official_sites()
    {
        var p = Provider("bing", TestResults.Result("Samsung", "https://samsung.com", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p });

        var response = await service.SearchAsync(new SearchRequest { Query = "samsung" });

        Assert.True(response.Results[0].IsOfficial);
        Assert.True(response.Results[0].Confidence >= 0.9);
    }

    [Fact]
    public async Task Rejects_fake_sites()
    {
        var p = Provider("bing", TestResults.Result("Fake", "https://samsung-login.xyz", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p });

        var response = await service.SearchAsync(new SearchRequest { Query = "samsung" });

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Provider_failure_does_not_break_search()
    {
        var failing = Provider("bing");
        failing.Throw = new InvalidOperationException("boom");
        var ok = Provider("duckduckgo", TestResults.Result("A", "https://sample-news.example.org/ok", "duckduckgo"));
        var service = CreateService(new IWebSearchProvider[] { failing, ok });

        var response = await service.SearchAsync(new SearchRequest { Query = "test" });

        Assert.Single(response.Results);
    }

    [Fact]
    public async Task Cache_hit_returns_cached_response()
    {
        var p = Provider("duckduckgo", TestResults.Result("A", "https://a.com", "duckduckgo"));
        var cache = new SearchCache();
        var service = CreateService(new IWebSearchProvider[] { p }, cache: cache);

        var first = await service.SearchAsync(new SearchRequest { Query = "cached" });
        var second = await service.SearchAsync(new SearchRequest { Query = "cached" });

        Assert.Equal(1, p.Calls);
        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
    }

    [Fact]
    public async Task Invalidate_cache_forces_new_search()
    {
        var p = Provider("duckduckgo", TestResults.Result("A", "https://a.com", "duckduckgo"));
        var cache = new SearchCache();
        var service = CreateService(new IWebSearchProvider[] { p }, cache: cache);

        await service.SearchAsync(new SearchRequest { Query = "cached" });
        service.InvalidateCache();
        await service.SearchAsync(new SearchRequest { Query = "cached" });

        Assert.Equal(2, p.Calls);
    }

    [Fact]
    public async Task Verification_can_be_disabled()
    {
        var p = Provider("bing", TestResults.Result("A", "https://a.com", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p });

        var response = await service.SearchAsync(new SearchRequest { Query = "test", VerifyLinks = false });

        Assert.Single(response.Results);
        Assert.Equal(LinkStatus.Unknown, response.Results[0].LinkStatus);
    }

    [Fact]
    public async Task Uses_suggested_provider_for_video_type()
    {
        var youtube = Provider("youtube", TestResults.Result("Video", "https://youtube.com/watch?v=1", "youtube"));
        youtube.Handle = true;
        var bing = Provider("bing", TestResults.Result("B", "https://b.com", "bing"));
        bing.Handle = true;
        var service = CreateService(new IWebSearchProvider[] { youtube, bing });

        var response = await service.SearchAsync(new SearchRequest { Query = "video", Type = SearchResultType.Video });

        Assert.Equal(1, youtube.Calls);
        Assert.Equal(0, bing.Calls);
    }

    [Fact]
    public async Task Resolve_official_returns_canonical_url()
    {
        var service = CreateService();

        var match = await service.ResolveOfficialAsync("le github d'Ollama");

        Assert.NotNull(match);
        Assert.Equal("https://github.com/ollama/ollama", match!.Url);
    }

    [Fact]
    public async Task Verify_link_delegates_to_verifier()
    {
        var verifier = new FakeLinkVerifier { Valid = true };
        var service = CreateService(verifier: verifier);

        var ok = await service.VerifyLinkAsync("https://example.com");

        Assert.True(ok);
        Assert.Equal(1, verifier.Calls);
    }

    [Fact]
    public async Task Respects_explicit_provider_selection()
    {
        var p1 = Provider("duckduckgo", TestResults.Result("A", "https://a.com", "duckduckgo"));
        var p2 = Provider("bing", TestResults.Result("B", "https://b.com", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p1, p2 });

        var response = await service.SearchAsync(new SearchRequest { Query = "test", ProviderNames = new[] { "bing" } });

        Assert.Equal(0, p1.Calls);
        Assert.Equal(1, p2.Calls);
    }
}
