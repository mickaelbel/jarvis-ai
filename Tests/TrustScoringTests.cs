using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class TrustScoringTests
{
    [Theory]
    [InlineData("wikipedia", 0.95)]
    [InlineData("github", 0.95)]
    [InlineData("arxiv", 0.95)]
    [InlineData("stackoverflow", 0.90)]
    [InlineData("youtube", 0.85)]
    [InlineData("duckduckgo", 0.80)]
    [InlineData("bing", 0.80)]
    [InlineData("reddit", 0.60)]
    [InlineData("unknown", 0.75)]
    public void Provider_trust_levels(string provider, double expected)
    {
        Assert.Equal(expected, TrustScoring.ProviderTrust(provider));
    }

    [Theory]
    [InlineData("https://www.samsung.com", 1.0)]
    [InlineData("https://www.parcoursup.fr", 1.0)]
    [InlineData("https://www.gouvernement.fr", 0.7)]
    [InlineData("https://example.edu", 0.95)]
    [InlineData("https://fr.wikipedia.org/wiki/X", 1.0)]
    [InlineData("https://random-blog.com", 0.7)]
    public void Domain_trust_scoring(string url, double expected)
    {
        Assert.Equal(expected, TrustScoring.DomainTrust(url));
    }

    [Fact]
    public void Official_results_boost_confidence()
    {
        var official = TestResults.Result("T", "https://openai.com", "duckduckgo");
        official.IsOfficial = true;
        var regular = TestResults.Result("T", "https://blog.example.com", "duckduckgo");

        Assert.True(TrustScoring.ComputeConfidence(official, isVerified: true) > TrustScoring.ComputeConfidence(regular, isVerified: true));
    }

    [Fact]
    public void Verified_links_boost_confidence()
    {
        var r = TestResults.Result("T", "https://example.com", "duckduckgo");

        Assert.True(TrustScoring.ComputeConfidence(r, isVerified: true) > TrustScoring.ComputeConfidence(r, isVerified: false));
    }

    [Fact]
    public void Recency_boosts_confidence()
    {
        var fresh = TestResults.Result("T", "https://example.com", "bing", published: DateTimeOffset.UtcNow.AddDays(-1));
        var old = TestResults.Result("T", "https://example.com", "bing", published: DateTimeOffset.UtcNow.AddYears(-5));

        Assert.True(TrustScoring.ComputeConfidence(fresh, isVerified: false) > TrustScoring.ComputeConfidence(old, isVerified: false));
    }

    [Fact]
    public void Confidence_clamped_to_range()
    {
        var r = TestResults.Result("T", "https://openai.com", "wikipedia");
        r.IsOfficial = true;

        var score = TrustScoring.ComputeConfidence(r, isVerified: true);

        Assert.InRange(score, 0.0, 1.0);
    }

    [Theory]
    [InlineData(1, 1, 0.7)]
    [InlineData(2, 2, 1.0)]
    [InlineData(1, 4, 0.25)]
    public void Normalized_agreement(double agreeing, double total, double expected)
    {
        Assert.Equal(expected, TrustScoring.NormalizedAgreement((int)agreeing, (int)total));
    }
}
