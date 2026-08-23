using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class FakeSiteDetectionTests
{
    private readonly FakeSiteDetector _detector = new();

    [Theory]
    [InlineData("https://discord.com", 1.0)]
    [InlineData("https://www.discord.com", 1.0)]
    [InlineData("https://www.samsung.com", 1.0)]
    [InlineData("https://github.com", 1.0)]
    [InlineData("https://mozilla.org", 1.0)]
    [InlineData("https://www.parcoursup.fr", 1.0)]
    public void Known_official_domains_score_full_trust(string url, double expected)
    {
        Assert.Equal(expected, _detector.ComputeTrustScore(new Uri(url)));
    }

    [Theory]
    [InlineData("https://disc0rd.com")]
    [InlineData("https://samsunng.com")]
    [InlineData("https://youube.com")]
    [InlineData("https://gihub.com")]
    [InlineData("https://microsoft-support.com")]
    [InlineData("https://samsung-login.xyz")]
    [InlineData("https://youtube-verify.com")]
    public void Typosquatting_domains_are_flagged(string url)
    {
        Assert.True(_detector.IsLikelyFake(url));
    }

    [Theory]
    [InlineData("https://discord.com")]
    [InlineData("https://openai.com")]
    [InlineData("https://stackoverflow.com")]
    [InlineData("https://www.bbc.com")]
    public void Real_official_domains_not_flagged(string url)
    {
        Assert.False(_detector.IsLikelyFake(url));
    }

    [Theory]
    [InlineData("https://samsung.com", "samsung.com")]
    [InlineData("https://www.github.com", "github.com")]
    [InlineData("https://disc0rd.com", "discord.com")]
    public void Finds_closest_official_domain(string url, string expected)
    {
        var host = new Uri(url).Host;
        var closest = _detector.FindClosestOfficialDomain(host);

        Assert.NotNull(closest);
        Assert.Equal(expected, closest);
    }

    [Fact]
    public void Suspicious_suffix_drops_trust()
    {
        var score = _detector.ComputeTrustScore(new Uri("https://samsung-official123.com"));

        Assert.True(score < 0.5);
    }

    [Fact]
    public void Unknown_legit_domain_scores_high_enough()
    {
        var score = _detector.ComputeTrustScore(new Uri("https://my-cool-blog.com"));

        Assert.True(score >= 0.5);
    }

    [Fact]
    public void Http_official_domain_is_penalized()
    {
        var score = _detector.ComputeTrustScore(new Uri("http://samsung.com"));

        Assert.True(score < 1.0);
    }

    [Fact]
    public void Insecure_http_preferred_site_is_flagged_insecure()
    {
        Assert.True(_detector.ComputeTrustScore(new Uri("http://samsung.com")) < _detector.ComputeTrustScore(new Uri("https://samsung.com")));
    }

    [Fact]
    public void Random_ip_address_not_likely_fake()
    {
        Assert.False(_detector.IsLikelyFake("https://192.168.1.1/admin"));
    }
}
