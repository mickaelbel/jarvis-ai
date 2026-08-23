using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class RankingAdvancedTests
{
    private readonly ResultRanker _ranker = new();

    [Fact]
    public void Media_results_get_bonus()
    {
        var regular = TestResults.Result("Regular", "https://example.com/1", "bing");
        regular.Confidence = 0.7;
        var media = TestResults.Result("Media", "https://www.lemonde.fr/actu", "bing");
        media.Confidence = 0.7;

        var ranked = _ranker.Rank(new[] { regular, media });

        Assert.Equal("Media", ranked[0].Title);
    }

    [Fact]
    public void Docs_results_get_bonus()
    {
        var regular = TestResults.Result("Regular", "https://example.com/1", "bing");
        regular.Confidence = 0.7;
        var docs = TestResults.Result("Docs", "https://docs.python.org/3/tutorial/", "bing");
        docs.Confidence = 0.7;

        var ranked = _ranker.Rank(new[] { regular, docs });

        Assert.Equal("Docs", ranked[0].Title);
    }

    [Fact]
    public void Pdf_results_get_bonus()
    {
        var regular = TestResults.Result("Regular", "https://x.com/1", "bing");
        regular.Confidence = 0.7;
        var pdf = TestResults.Result("Pdf", "https://x.com/doc.pdf", "bing");
        pdf.Confidence = 0.7;
        pdf = new SearchResult
        {
            Title = "Pdf",
            Url = "https://x.com/doc.pdf",
            Snippet = "snippet",
            Source = "x.com",
            Provider = "bing",
            Confidence = 0.7,
            ContentType = "pdf"
        };

        var ranked = _ranker.Rank(new[] { regular, pdf });

        Assert.Equal("Pdf", ranked[0].Title);
    }

    [Fact]
    public void Video_results_get_bonus()
    {
        var regular = TestResults.Result("Regular", "https://example.com/1", "bing");
        regular.Confidence = 0.7;
        var video = TestResults.Result("Video", "https://www.youtube.com/watch?v=1", "youtube");
        video.Confidence = 0.7;
        video.Type = SearchResultType.Video;

        var ranked = _ranker.Rank(new[] { regular, video });

        Assert.Equal("Video", ranked[0].Title);
    }

    [Fact]
    public void Relevant_results_rank_higher()
    {
        var irrelevant = TestResults.Result("Recette de cuisine", "https://example.com/1", "bing");
        irrelevant.Confidence = 0.8;
        var relevant = TestResults.Result("Guide Python pour développeurs", "https://example.com/2", "bing");
        relevant.Confidence = 0.8;

        var ranked = _ranker.Rank(new[] { irrelevant, relevant }, query: "python");

        Assert.Equal("Guide Python pour développeurs", ranked[0].Title);
    }

    [Fact]
    public void Recent_results_get_recency_bonus()
    {
        var old = TestResults.Result("Old", "https://example.com/1", "bing", published: DateTimeOffset.UtcNow.AddYears(-2));
        old.Confidence = 0.7;
        var recent = TestResults.Result("Recent", "https://example.com/2", "bing", published: DateTimeOffset.UtcNow.AddDays(-2));
        recent.Confidence = 0.7;

        var ranked = _ranker.Rank(new[] { old, recent });

        Assert.Equal("Recent", ranked[0].Title);
    }

    [Fact]
    public void Fake_sites_are_penalized()
    {
        var legit = TestResults.Result("Legit", "https://x.com/1", "bing");
        legit.Confidence = 0.7;
        var fake = TestResults.Result("Fake", "https://samsung-login.xyz/secure", "bing");
        fake.Confidence = 0.9;

        var ranked = _ranker.Rank(new[] { legit, fake }, fakeSiteDetector: new FakeSiteDetector());

        Assert.Equal("Legit", ranked[0].Title);
    }

    [Fact]
    public void Prefer_official_boosts_official_results()
    {
        var regular = TestResults.Result("Regular", "https://example.com/1", "bing");
        regular.Confidence = 0.7;
        var official = TestResults.Result("Official", "https://samsung.com", "bing");
        official.Confidence = 0.7;
        official.IsOfficial = true;

        var withPreference = _ranker.Rank(new[] { regular, official }, preferOfficial: true);
        Assert.Equal("Official", withPreference[0].Title);
    }

    [Fact]
    public void Academic_results_get_bonus()
    {
        var regular = TestResults.Result("Regular", "https://example.com/1", "bing");
        regular.Confidence = 0.7;
        var academic = TestResults.Result("Paper", "https://arxiv.org/abs/1234", "arxiv");
        academic.Confidence = 0.7;
        academic.Type = SearchResultType.Academic;

        var ranked = _ranker.Rank(new[] { regular, academic });

        Assert.Equal("Paper", ranked[0].Title);
    }

    [Fact]
    public void Final_score_is_never_negative()
    {
        var fake = TestResults.Result("Fake", "https://samsung-login.xyz/secure", "bing");
        fake.Confidence = 0.1;

        var ranked = _ranker.Rank(new[] { fake }, fakeSiteDetector: new FakeSiteDetector());

        Assert.True(ranked[0].FinalScore >= 0);
    }

    [Fact]
    public void Ties_broken_by_publish_date()
    {
        var old = TestResults.Result("Old", "https://example.com/1", "bing", published: DateTimeOffset.UtcNow.AddDays(-30));
        var recent = TestResults.Result("Recent", "https://example.com/2", "bing", published: DateTimeOffset.UtcNow.AddDays(-1));
        old.Confidence = 0.5;
        recent.Confidence = 0.5;

        var ranked = _ranker.Rank(new[] { old, recent });

        Assert.Equal("Recent", ranked[0].Title);
    }
}
