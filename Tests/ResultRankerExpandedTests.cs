using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class ResultRankerExpandedTests
{
    private readonly ResultRanker _ranker = new();

    [Fact]
    public void Higher_confidence_ranks_first()
    {
        var low = TestResults.Result("B", "https://x.com/b", "duckduckgo");
        low.Confidence = 0.4;
        var high = TestResults.Result("A", "https://x.com/a", "duckduckgo");
        high.Confidence = 0.9;

        var ranked = _ranker.Rank(new[] { low, high });

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void Official_ranks_above_non_official()
    {
        var plain = TestResults.Result("B", "https://x.com/b", "duckduckgo");
        plain.Confidence = 0.7;
        var official = TestResults.Result("A", "https://x.com/a", "duckduckgo");
        official.Confidence = 0.7;
        official.IsOfficial = true;

        var ranked = _ranker.Rank(new[] { plain, official });

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void Verified_ranks_above_unverified()
    {
        var unverified = TestResults.Result("B", "https://x.com/b", "duckduckgo");
        unverified.Confidence = 0.7;
        var verified = TestResults.Result("A", "https://x.com/a", "duckduckgo");
        verified.Confidence = 0.7;
        verified.IsVerified = true;

        var ranked = _ranker.Rank(new[] { unverified, verified });

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void Media_domain_gets_bonus()
    {
        var plain = TestResults.Result("B", "https://x.com/b", "duckduckgo");
        plain.Confidence = 0.7;
        var media = TestResults.Result("A", "https://lemonde.fr/article", "duckduckgo");
        media.Confidence = 0.7;

        var ranked = _ranker.Rank(new[] { plain, media });

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void Docs_host_gets_bonus()
    {
        var plain = TestResults.Result("B", "https://x.com/b", "duckduckgo");
        plain.Confidence = 0.7;
        var docs = TestResults.Result("A", "https://docs.python.org/guide", "duckduckgo");
        docs.Confidence = 0.7;

        var ranked = _ranker.Rank(new[] { plain, docs });

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void Content_type_docs_gets_bonus()
    {
        var plain = TestResults.Result("B", "https://x.com/b", "duckduckgo");
        plain.Confidence = 0.7;
        var docs = new SearchResult
        {
            Title = "A",
            Url = "https://x.com/a",
            Snippet = "s",
            Provider = "duckduckgo",
            ContentType = "docs",
            Confidence = 0.7
        };

        var ranked = _ranker.Rank(new[] { plain, docs });

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void Pdf_content_gets_boost()
    {
        var plain = TestResults.Result("B", "https://x.com/b", "duckduckgo");
        plain.Confidence = 0.7;
        var pdf = new SearchResult
        {
            Title = "A",
            Url = "https://x.com/a.pdf",
            Snippet = "s",
            Provider = "duckduckgo",
            ContentType = "pdf",
            Confidence = 0.7
        };

        var ranked = _ranker.Rank(new[] { plain, pdf });

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void Recency_ranks_newer_higher()
    {
        var old = TestResults.Result("B", "https://x.com/b", "duckduckgo", published: DateTimeOffset.UtcNow.AddYears(-2));
        old.Confidence = 0.7;
        var fresh = TestResults.Result("A", "https://x.com/a", "duckduckgo", published: DateTimeOffset.UtcNow);
        fresh.Confidence = 0.7;

        var ranked = _ranker.Rank(new[] { old, fresh });

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void Academic_type_gets_boost()
    {
        var plain = TestResults.Result("B", "https://x.com/b", "duckduckgo");
        plain.Confidence = 0.7;
        var academic = TestResults.Result("A", "https://x.com/a", "arxiv");
        academic.Confidence = 0.7;
        academic.Type = SearchResultType.Academic;

        var ranked = _ranker.Rank(new[] { plain, academic });

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void Video_type_gets_boost()
    {
        var plain = TestResults.Result("B", "https://x.com/b", "duckduckgo");
        plain.Confidence = 0.7;
        var video = TestResults.Result("A", "https://x.com/a", "youtube");
        video.Confidence = 0.7;
        video.Type = SearchResultType.Video;

        var ranked = _ranker.Rank(new[] { plain, video });

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void Official_repo_gets_big_boost()
    {
        var custom = TestResults.Result("B", "https://github.com/someone/custom", "github");
        custom.Confidence = 0.7;
        custom.Type = SearchResultType.Repository;
        var official = TestResults.Result("A", "https://github.com/ollama/ollama", "github");
        official.Confidence = 0.7;
        official.Type = SearchResultType.Repository;
        official.IsOfficial = true;

        var ranked = _ranker.Rank(new[] { custom, official });

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void Official_channel_gets_big_boost()
    {
        var custom = TestResults.Result("B", "https://www.youtube.com/@someone", "youtube");
        custom.Confidence = 0.7;
        custom.Type = SearchResultType.Channel;
        var official = TestResults.Result("A", "https://www.youtube.com/@MrBeast", "youtube");
        official.Confidence = 0.7;
        official.Type = SearchResultType.Channel;
        official.IsOfficial = true;

        var ranked = _ranker.Rank(new[] { custom, official });

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void School_domain_gets_boost()
    {
        var plain = TestResults.Result("B", "https://x.com/b", "duckduckgo");
        plain.Confidence = 0.7;
        var school = TestResults.Result("A", "https://www.ac-bordeaux.fr/lycee", "duckduckgo");
        school.Confidence = 0.7;

        var ranked = _ranker.Rank(new[] { plain, school });

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void Construction_domain_gets_boost_when_official()
    {
        var other = TestResults.Result("B", "https://x.com/b", "duckduckgo");
        other.Confidence = 0.7;
        other.IsOfficial = true;
        var builder = TestResults.Result("A", "https://www.apple.com/macbook", "duckduckgo");
        builder.Confidence = 0.7;
        builder.IsOfficial = true;

        var ranked = _ranker.Rank(new[] { other, builder });

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void Fake_site_is_penalized()
    {
        var detector = new FakeSiteDetector();
        var legit = TestResults.Result("A", "https://microsoft.com/support", "duckduckgo");
        legit.Confidence = 0.7;
        var phishing = TestResults.Result("B", "https://micros0ft.com/support", "duckduckgo");
        phishing.Confidence = 0.7;

        var ranked = _ranker.Rank(new[] { phishing, legit }, fakeSiteDetector: detector);

        Assert.Equal("A", ranked[0].Title);
    }

    [Fact]
    public void Final_score_is_never_negative()
    {
        var poor = TestResults.Result("A", "https://micros0ft.com/support", "duckduckgo");
        poor.Confidence = 0.0;
        poor.IsOfficial = false;
        poor.IsVerified = false;

        var ranked = _ranker.Rank(new[] { poor }, fakeSiteDetector: new FakeSiteDetector());

        Assert.True(ranked[0].FinalScore >= 0);
    }

    [Fact]
    public void Official_still_ranks_first_without_prefer_official()
    {
        var plain = TestResults.Result("B", "https://x.com/b", "duckduckgo");
        plain.Confidence = 0.7;
        var official = TestResults.Result("A", "https://x.com/a", "duckduckgo");
        official.Confidence = 0.7;
        official.IsOfficial = true;

        var ranked = _ranker.Rank(new[] { plain, official }, preferOfficial: false);

        Assert.Equal("A", ranked[0].Title);
    }

    [Theory]
    [InlineData("install python", "Install Python on Windows", "Random cooking recipe")]
    [InlineData("recette crepes", "Recette des crêpes facile", "How to fix a laptop")]
    [InlineData("ollama", "Guide complet sur Ollama", "Météo du jour")]
    public void Relevance_ranks_matching_title_first(string query, string matchTitle, string otherTitle)
    {
        var match = TestResults.Result(matchTitle, "https://x.com/a", "duckduckgo");
        match.Confidence = 0.7;
        var other = TestResults.Result(otherTitle, "https://x.com/b", "duckduckgo");
        other.Confidence = 0.7;

        var ranked = _ranker.Rank(new[] { other, match }, query: query);

        Assert.Equal(matchTitle, ranked[0].Title);
    }
}
