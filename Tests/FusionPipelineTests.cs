using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class FusionPipelineTests
{
    private static WebSearchService CreateService(
        IReadOnlyList<IWebSearchProvider>? providers = null,
        FakeLinkVerifier? verifier = null,
        ISearchCache? cache = null,
        SearchCacheOptions? options = null)
        => new(
            providers ?? Array.Empty<IWebSearchProvider>(),
            verifier ?? new FakeLinkVerifier(),
            cache ?? new SearchCache(),
            new OfficialSiteDetector(),
            new FakeSiteDetector(),
            NullLogger<WebSearchService>.Instance,
            options);

    private static StubSearchProvider Provider(string name, params SearchResult[] results)
        => new(name, results);

    [Fact]
    public async Task Near_duplicates_across_providers_are_merged()
    {
        var p1 = Provider("duckduckgo", TestResults.Result("Comment installer Python sur Windows", "https://x.com/1", "duckduckgo"));
        var p2 = Provider("bing", TestResults.Result("Comment installer Python sur Windows", "https://x.com/2", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p1, p2 });

        var response = await service.SearchAsync(new SearchRequest { Query = "installer python" });

        Assert.Single(response.Results);
        Assert.Equal(2, response.Results[0].MergedCount);
    }

    [Fact]
    public async Task Merged_count_reflects_deduplicated_results()
    {
        var p1 = Provider("duckduckgo", TestResults.Result("A", "https://x.com/1", "duckduckgo"), TestResults.Result("B", "https://x.com/2", "duckduckgo"));
        var p2 = Provider("bing", TestResults.Result("B", "https://x.com/2", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p1, p2 });

        var response = await service.SearchAsync(new SearchRequest { Query = "test" });

        Assert.Equal(3, response.TotalProviderResults);
        Assert.Equal(1, response.MergedCount);
    }

    [Fact]
    public async Task Winner_provider_is_top_ranked_source()
    {
        var p1 = Provider("duckduckgo", TestResults.Result("Low", "https://sample-blog.example.net/low", "duckduckgo"));
        var p2 = Provider("bing", TestResults.Result("High", "https://www.lemonde.fr/high", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p1, p2 });

        var response = await service.SearchAsync(new SearchRequest { Query = "test" });

        Assert.Equal("bing", response.WinnerProvider);
    }

    [Fact]
    public async Task Provider_stats_record_successes()
    {
        var p = Provider("bing", TestResults.Result("A", "https://sample-news.example.org/a", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p });

        var response = await service.SearchAsync(new SearchRequest { Query = "test" });

        var stat = Assert.Single(response.ProviderStats);
        Assert.Equal("bing", stat.Provider);
        Assert.True(stat.Succeeded);
        Assert.Equal(1, stat.ResultCount);
    }

    [Fact]
    public async Task Provider_stats_record_failures()
    {
        var failing = Provider("bing");
        failing.Throw = new InvalidOperationException("boom");
        var service = CreateService(new IWebSearchProvider[] { failing });

        await service.SearchAsync(new SearchRequest { Query = "test" });

        var stat = Assert.Single(service.GetProviderHealth().Values);
        Assert.Equal(1, stat.Failures);
    }

    [Fact]
    public async Task Provider_health_tracks_calls()
    {
        var p = Provider("duckduckgo", TestResults.Result("A", "https://sample-news.example.org/a", "duckduckgo"));
        var service = CreateService(new IWebSearchProvider[] { p });

        await service.SearchAsync(new SearchRequest { Query = "one" });
        await service.SearchAsync(new SearchRequest { Query = "two" });

        var health = service.GetProviderHealth()["duckduckgo"];
        Assert.Equal(2, health.Calls);
        Assert.Equal(0, health.Failures);
    }

    [Fact]
    public async Task Invalidate_provider_clears_only_that_provider_entries()
    {
        var p1 = Provider("duckduckgo", TestResults.Result("A", "https://sample-news.example.org/a", "duckduckgo"));
        var p2 = Provider("bing", TestResults.Result("B", "https://sample-blog.example.org/b", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p1, p2 });

        await service.SearchAsync(new SearchRequest { Query = "q", ProviderNames = new[] { "duckduckgo" } });
        await service.SearchAsync(new SearchRequest { Query = "q", ProviderNames = new[] { "bing" } });

        service.InvalidateProvider("bing");

        await service.SearchAsync(new SearchRequest { Query = "q", ProviderNames = new[] { "duckduckgo" } });
        await service.SearchAsync(new SearchRequest { Query = "q", ProviderNames = new[] { "bing" } });

        Assert.Equal(1, p1.Calls);
        Assert.Equal(2, p2.Calls);
    }

    [Fact]
    public async Task Prefetch_re_searches_hot_queries()
    {
        var p = Provider("duckduckgo", TestResults.Result("A", "https://sample-news.example.org/a", "duckduckgo"));
        var service = CreateService(new IWebSearchProvider[] { p });

        await service.SearchAsync(new SearchRequest { Query = "hot" });
        await service.SearchAsync(new SearchRequest { Query = "hot" });

        await service.PrefetchHotQueriesAsync(1);

        Assert.Equal(2, p.Calls);
    }

    [Fact]
    public async Task Content_type_is_assigned_when_missing()
    {
        var p = Provider("bing", TestResults.Result("Doc", "https://x.com/guide.pdf", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p });

        var response = await service.SearchAsync(new SearchRequest { Query = "test" });

        Assert.Equal("pdf", response.Results[0].ContentType);
    }

    [Fact]
    public async Task Total_groups_reflects_topic_grouping()
    {
        var p = Provider("bing",
            TestResults.Result("Tutoriel Python débutant", "https://sample-blog.example.org/1", "bing"),
            TestResults.Result("Recette de crêpes", "https://sample-blog.example.org/2", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p });

        var response = await service.SearchAsync(new SearchRequest { Query = "test" });

        Assert.Equal(2, response.TotalGroups);
    }

    [Fact]
    public async Task Consensus_agreement_is_populated()
    {
        var p = Provider("bing", TestResults.Result("Résultat", "https://sample-news.example.org/a", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p });

        var response = await service.SearchAsync(new SearchRequest { Query = "test" });

        Assert.InRange(response.ConsensusAgreement, 0.0, 1.0);
    }

    [Fact]
    public async Task Cache_key_includes_provider_selection()
    {
        var p = Provider("bing", TestResults.Result("A", "https://sample-blog.example.org/a", "bing"));
        var service = CreateService(new IWebSearchProvider[] { p });

        await service.SearchAsync(new SearchRequest { Query = "q", ProviderNames = new[] { "bing" } });
        await service.SearchAsync(new SearchRequest { Query = "q", ProviderNames = new[] { "bing" } });
        await service.SearchAsync(new SearchRequest { Query = "q" });

        Assert.Equal(2, p.Calls);
    }

    [Fact]
    public async Task Local_results_carry_open_now_flag_through_pipeline()
    {
        var result = new SearchResult
        {
            Title = "Pharma",
            Url = "https://www.openstreetmap.org/?mlat=44.8&mlon=-0.5",
            Snippet = "pharmacie · Ouvert",
            Source = "openstreetmap.org",
            Provider = "nominatim",
            Type = SearchResultType.Local,
            OpenNow = true,
            ExtraScore = 0.8
        };
        var p = Provider("nominatim", result);
        var service = CreateService(new IWebSearchProvider[] { p });

        var response = await service.SearchAsync(new SearchRequest { Query = "pharmacie", Type = SearchResultType.Local });

        Assert.True(response.Results[0].OpenNow);
        Assert.Equal(SearchResultType.Local, response.Results[0].Type);
    }
}
