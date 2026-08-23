using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class TrustScoringExpandedTests
{
    [Theory]
    [InlineData("wikipedia", 0.95)]
    [InlineData("wikidata", 0.95)]
    [InlineData("github", 0.95)]
    [InlineData("arxiv", 0.95)]
    [InlineData("stackoverflow", 0.90)]
    [InlineData("stack exchange", 0.90)]
    [InlineData("google news", 0.90)]
    [InlineData("news", 0.88)]
    [InlineData("rss", 0.88)]
    [InlineData("youtube", 0.85)]
    [InlineData("nominatim", 0.85)]
    [InlineData("openstreetmap", 0.85)]
    [InlineData("duckduckgo", 0.80)]
    [InlineData("bing", 0.80)]
    [InlineData("reddit", 0.60)]
    [InlineData("unknown", 0.75)]
    public void Provider_trust_matches_matrix(string provider, double expected)
    {
        Assert.Equal(expected, TrustScoring.ProviderTrust(provider), precision: 2);
    }

    [Theory]
    [InlineData("https://www.example.gov", 0.98)]
    [InlineData("https://example.gouv.fr", 0.98)]
    [InlineData("https://university.edu", 0.95)]
    [InlineData("https://github.com", 1.0)]
    [InlineData("https://www.microsoft.com", 1.0)]
    [InlineData("https://wikipedia.org", 1.0)]
    [InlineData("https://stackoverflow.com", 1.0)]
    [InlineData("https://openstreetmap.org", 0.85)]
    [InlineData("https://example.com", 0.7)]
    [InlineData("not a url", 0.0)]
    public void Domain_trust_matches_matrix(string url, double expected)
    {
        Assert.Equal(expected, TrustScoring.DomainTrust(url), precision: 2);
    }

    [Fact]
    public void Official_result_gets_high_confidence()
    {
        var result = TestResults.Result("Title", "https://x.com/a", "duckduckgo");
        result.IsOfficial = true;
        Assert.True(TrustScoring.ComputeConfidence(result, isVerified: false) >= 0.9);
    }

    [Fact]
    public void Verified_result_scores_higher()
    {
        var unverified = TestResults.Result("T", "https://x.com/a", "duckduckgo");
        var verified = TestResults.Result("T", "https://x.com/a", "duckduckgo");
        Assert.True(TrustScoring.ComputeConfidence(verified, isVerified: true) >
                    TrustScoring.ComputeConfidence(unverified, isVerified: false));
    }

    [Fact]
    public void Extra_score_is_added()
    {
        var plain = TestResults.Result("T", "https://x.com/a", "duckduckgo");
        var boosted = TestResults.Result("T", "https://x.com/a", "duckduckgo", extra: 0.2);
        Assert.True(TrustScoring.ComputeConfidence(boosted, isVerified: false) >
                    TrustScoring.ComputeConfidence(plain, isVerified: false));
    }

    [Fact]
    public void Recent_result_scores_higher_than_old_result()
    {
        var recent = TestResults.Result("T", "https://x.com/a", "duckduckgo", published: DateTimeOffset.UtcNow.AddDays(-3));
        var old = TestResults.Result("T", "https://x.com/a", "duckduckgo", published: DateTimeOffset.UtcNow.AddDays(-800));
        Assert.True(TrustScoring.ComputeConfidence(recent, isVerified: false) >
                    TrustScoring.ComputeConfidence(old, isVerified: false));
    }

    [Fact]
    public void Confidence_is_clamped_to_one()
    {
        var result = TestResults.Result("T", "https://github.com/ollama/ollama", "github", extra: 5.0);
        result.IsOfficial = true;
        Assert.Equal(1.0, TrustScoring.ComputeConfidence(result, isVerified: true), precision: 4);
    }

    [Theory]
    [InlineData(2, 3, 0.6667)]
    [InlineData(3, 3, 1.0)]
    [InlineData(1, 1, 0.7)]
    [InlineData(0, 1, 0.0)]
    [InlineData(0, 0, 0.0)]
    [InlineData(4, 8, 0.5)]
    public void Normalized_agreement_matches_matrix(int agreeing, int total, double expected)
    {
        Assert.Equal(expected, TrustScoring.NormalizedAgreement(agreeing, total), precision: 3);
    }
}
