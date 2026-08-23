using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public class EntityConsolidationTests
{
    private static SearchResult Channel(string url, long? subs = null, long? videos = null,
        bool verified = false, bool official = false, string provider = "p1",
        DateTimeOffset? created = null, double confidence = 0.9, double? extraScore = null)
        => new()
        {
            Title = "Chaîne YouTube",
            Url = url,
            Source = "http://youtube.com",
            Provider = provider,
            Type = SearchResultType.Channel,
            SubscriberCount = subs,
            VideoCount = videos,
            ChannelPublishedAt = created,
            IsVerified = verified,
            IsOfficial = official,
            Confidence = confidence,
            ExtraScore = extraScore
        };

    [Fact]
    public void Same_handle_from_multiple_providers_is_consolidated_to_single_representative()
    {
        var results = new List<SearchResult>
        {
            Channel("https://www.youtube.com/@MrBeast", subs: 120_000_000, verified: true, official: true, provider: "duckduckgo"),
            Channel("https://www.youtube.com/@MrBeast", subs: 60_000_000, verified: true, official: true, provider: "bing"),
        };

        var consolidated = new EntityConsolidator().Consolidate(results, providerContextCount: 2);

        Assert.Single(consolidated);
        Assert.Contains("@MrBeast", consolidated[0].Url);
        Assert.Equal(2, consolidated[0].MergedCount);
        Assert.Equal(1.0, consolidated[0].EntityAgreement);
    }

    [Fact]
    public void Handle_identity_is_case_insensitive_and_best_metadata_wins()
    {
        var results = new List<SearchResult>
        {
            Channel("https://www.youtube.com/@mrbeast", subs: 400_000_000, videos: 900, verified: true, official: true, created: new DateTimeOffset(2016, 1, 1, 0, 0, 0, TimeSpan.Zero), provider: "duckduckgo"),
            Channel("https://www.youtube.com/@MrBeast", subs: 410_000_000, videos: 910, verified: true, official: true, created: new DateTimeOffset(2016, 1, 1, 0, 0, 0, TimeSpan.Zero), provider: "bing"),
        };

        var consolidated = new EntityConsolidator().Consolidate(results, 2);

        Assert.Single(consolidated);
        Assert.Contains("@MrBeast", consolidated[0].Url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, consolidated[0].MergedCount);
        Assert.Equal(410_000_000L, consolidated[0].SubscriberCount);
    }

    [Fact]
    public void Fake_user_channel_is_never_chosen_over_real_official_handle()
    {
        // Le faux profil (@user-) a beaucoup d'abonnés, mais l'officiel a la
        // vérification, l'officialité et l'accord multi-providers.
        var fake = Channel("https://www.youtube.com/@user-4x9f2k", subs: 999_999, videos: 3, verified: false, official: false, provider: "duckduckgo");
        var realA = Channel("https://www.youtube.com/@MrBeast", subs: 400_000_000, videos: 900, verified: true, official: true, created: new DateTimeOffset(2016, 1, 1, 0, 0, 0, TimeSpan.Zero), provider: "bing");
        var realB = Channel("https://www.youtube.com/@MrBeast", subs: 410_000_000, videos: 910, verified: true, official: true, created: new DateTimeOffset(2016, 1, 1, 0, 0, 0, TimeSpan.Zero), provider: "google");

        var consolidated = new EntityConsolidator().Consolidate(new[] { fake, realA, realB }, 3);

        var ranker = new ResultRanker();
        var ranked = ranker.Rank(consolidated, "chaîne MrBeast", SearchResultType.Channel, preferOfficial: true);

        Assert.Equal("https://www.youtube.com/@MrBeast", ranked[0].Url);
        Assert.Contains("@user", ranked[^1].Url, StringComparison.Ordinal);
        Assert.True(ranked[0].EntityAgreement > 0.5);
    }

    [Fact]
    public void Channel_id_and_handle_same_channel_group_together()
    {
        var results = new List<SearchResult>
        {
            Channel("https://www.youtube.com/channel/UCX6OQ3DkcsbYNE6H8uQQuVA", subs: 5_000_000, verified: true, provider: "duckduckgo"),
            Channel("https://www.youtube.com/@Veritasium", subs: 5_100_000, verified: true, provider: "bing"),
        };

        // Ces deux URL ne désignent PAS la même identité canonique (handle ≠ channel id) :
        // le consolidator ne les mélange pas à tort.
        var consolidated = new EntityConsolidator().Consolidate(results, 2);

        Assert.Equal(2, consolidated.Count);
    }

    [Fact]
    public void Non_youtube_and_general_results_pass_through_unchanged()
    {
        var results = new List<SearchResult>
        {
            new() { Title = "Samsung", Url = "https://www.samsung.com/fr/", Provider = "duckduckgo", Type = SearchResultType.OfficialSite, IsOfficial = true },
            new() { Title = "Article", Url = "https://example.com/a", Provider = "bing", Type = SearchResultType.General },
        };

        var consolidated = new EntityConsolidator().Consolidate(results, 2);

        Assert.Equal(2, consolidated.Count);
        Assert.Equal("https://www.samsung.com/fr/", consolidated[0].Url);
        Assert.Equal("https://example.com/a", consolidated[1].Url);
    }

    [Fact]
    public void Same_entity_from_two_providers_gets_agreement_boost_and_ranks_first()
    {
        var a = Channel("https://www.youtube.com/@MrBeast", subs: 400_000_000, videos: 900, verified: true, official: true, created: new DateTimeOffset(2016, 1, 1, 0, 0, 0, TimeSpan.Zero), provider: "duckduckgo");
        var b = Channel("https://www.youtube.com/@MrBeast", subs: 410_000_000, videos: 910, verified: true, official: true, created: new DateTimeOffset(2016, 1, 1, 0, 0, 0, TimeSpan.Zero), provider: "bing");

        var consolidated = new EntityConsolidator().Consolidate(new[] { a, b }, 2);
        Assert.Single(consolidated);
        Assert.Equal(1.0, consolidated[0].EntityAgreement);

        var ranked = new ResultRanker().Rank(consolidated, "MrBeast", SearchResultType.Channel, preferOfficial: true);
        Assert.Equal("https://www.youtube.com/@MrBeast", ranked[0].Url);
        Assert.True(ranked[0].FinalScore > 100);
    }
}