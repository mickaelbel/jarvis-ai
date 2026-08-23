using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class ResultRankerTests
{
    private readonly ResultRanker _ranker = new();

    [Fact]
    public void Sorts_by_final_score_descending()
    {
        var low = TestResults.Result("Low", "https://example.com/1", "bing");
        low.Confidence = 0.3;
        var high = TestResults.Result("High", "https://example.com/2", "bing");
        high.Confidence = 0.9;

        var ranked = _ranker.Rank(new[] { low, high });

        Assert.Equal("High", ranked[0].Title);
        Assert.True(ranked[0].FinalScore > ranked[1].FinalScore);
    }

    [Fact]
    public void Official_results_get_bonus()
    {
        var regular = TestResults.Result("Regular", "https://blog.example.com", "bing");
        regular.Confidence = 0.7;
        var official = TestResults.Result("Official", "https://samsung.com", "bing");
        official.Confidence = 0.6;
        official.IsOfficial = true;

        var ranked = _ranker.Rank(new[] { regular, official });

        Assert.Equal("Official", ranked[0].Title);
    }

    [Fact]
    public void Verified_results_rank_above_unverified()
    {
        var unverified = TestResults.Result("U", "https://example.com/1", "bing");
        unverified.Confidence = 0.8;
        var verified = TestResults.Result("V", "https://example.com/2", "bing");
        verified.Confidence = 0.8;
        verified.IsVerified = true;

        var ranked = _ranker.Rank(new[] { unverified, verified });

        Assert.Equal("V", ranked[0].Title);
    }

    [Fact]
    public void Official_site_type_gets_extra_bonus()
    {
        var general = TestResults.Result("G", "https://example.com", "bing");
        general.Confidence = 0.85;
        var officialSite = TestResults.Result("O", "https://example.com", "bing");
        officialSite.Confidence = 0.85;
        officialSite.Type = SearchResultType.OfficialSite;

        var ranked = _ranker.Rank(new[] { general, officialSite });

        Assert.Equal("O", ranked[0].Title);
    }

    [Fact]
    public void Assigns_final_score_to_all()
    {
        var results = new[]
        {
            TestResults.Result("A", "https://example.com", "bing"),
            TestResults.Result("B", "https://example.com", "bing")
        };
        foreach (var r in results)
            r.Confidence = 0.5;

        var ranked = _ranker.Rank(results);

        Assert.All(ranked, r => Assert.True(r.FinalScore > 0));
    }
}
