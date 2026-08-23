using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class OfficialSiteResolutionExpandedTests
{
    private readonly OfficialSiteDetector _detector = new();

    [Fact]
    public void Resolves_official_domain_from_utterance()
    {
        var match = _detector.ResolveOfficial("site officiel de microsoft");
        Assert.NotNull(match);
        Assert.Equal("official-domain", match!.MatchKind);
        Assert.Equal("https://microsoft.com", match.Url);
    }

    [Fact]
    public void Resolves_youtube_channel()
    {
        var match = _detector.ResolveOfficial("youtube chaîne MrBeast");
        Assert.NotNull(match);
        Assert.Equal("youtube-channel", match!.MatchKind);
        Assert.Equal("https://www.youtube.com/@MrBeast", match.Url);
    }

    [Fact]
    public void Resolves_channel_by_keyword()
    {
        var match = _detector.ResolveOfficial("chaîne de veritasium");
        Assert.NotNull(match);
        Assert.Equal("https://www.youtube.com/@veritasium", match!.Url);
    }

    [Fact]
    public void Resolves_github_repo()
    {
        var match = _detector.ResolveOfficial("github repo ollama");
        Assert.NotNull(match);
        Assert.Equal("github-repo", match!.MatchKind);
        Assert.Equal("https://github.com/ollama/ollama", match.Url);
    }

    [Fact]
    public void Resolves_repo_after_marker()
    {
        var match = _detector.ResolveOfficial("repo de ollama");
        Assert.NotNull(match);
        Assert.Equal("https://github.com/ollama/ollama", match!.Url);
    }

    [Fact]
    public void Resolves_school()
    {
        var match = _detector.ResolveOfficial("école polytechnique");
        Assert.NotNull(match);
        Assert.Equal("school", match!.MatchKind);
        Assert.Equal("https://www.polytechnique.edu", match.Url);
    }

    [Fact]
    public void Resolves_direct_url()
    {
        var match = _detector.ResolveOfficial("https://www.apple.com");
        Assert.NotNull(match);
        Assert.Equal("direct-url", match!.MatchKind);
        Assert.Equal("www.apple.com", match.Name);
        Assert.Equal(0.9, match.Confidence);
    }

    [Fact]
    public void Unresolvable_text_returns_null()
    {
        Assert.Null(_detector.ResolveOfficial("quelque chose d'anodin"));
    }

    [Theory]
    [InlineData("https://microsoft.com", true)]
    [InlineData("http://www.microsoft.com", true)]
    [InlineData("https://www.youtube.com/watch?v=x", true)]
    [InlineData("https://example.com", false)]
    [InlineData("https://notreal-domain.com", false)]
    [InlineData("not a url", false)]
    public void IsOfficialUrl_detects_official_hosts(string url, bool expected)
    {
        Assert.Equal(expected, _detector.IsOfficialUrl(url));
    }

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("www.microsoft.com", true)]
    [InlineData("YOUTUBE.COM", true)]
    [InlineData("evil.com", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsOfficialHost_detects_official_hosts(string host, bool expected)
    {
        Assert.Equal(expected, _detector.IsOfficialHost(host));
    }

    [Theory]
    [InlineData("microsoft office", "microsoft", true)]
    [InlineData("mon microsoftword", "microsoft", false)]
    [InlineData("microsoft", "microsoft", true)]
    [InlineData("libre office", "microsoft", false)]
    [InlineData("microsoft!", "microsoft", true)]
    public void ContainsWord_respects_boundaries(string text, string key, bool expected)
    {
        Assert.Equal(expected, OfficialSiteDetector.ContainsWord(text, key));
    }

    [Fact]
    public void Catalog_exposes_known_repositories()
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialRepo("ollama", out var repo));
        Assert.Equal("ollama/ollama", repo);
        Assert.True(OfficialSiteCatalog.TryGetOfficialRepo("whisper", out var whisper));
        Assert.Equal("openai/whisper", whisper);
    }

    [Fact]
    public void Catalog_exposes_known_channels()
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialChannel("mrbeast", out var url));
        Assert.Equal("https://www.youtube.com/@MrBeast", url);
    }

    [Fact]
    public void Catalog_exposes_known_schools()
    {
        Assert.True(OfficialSiteCatalog.TryGetSchoolUrl("polytechnique", out var url));
        Assert.Equal("https://www.polytechnique.edu", url);
    }

    [Fact]
    public void Catalog_exposes_known_domains()
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialDomain("microsoft", out var domain));
        Assert.Equal("microsoft.com", domain);
    }

    [Fact]
    public void All_domains_are_materialized_once()
    {
        var first = OfficialSiteCatalog.AllDomains;
        var second = OfficialSiteCatalog.AllDomains;
        Assert.Same(first, second);
        Assert.Contains("microsoft.com", first);
        Assert.Contains("youtube.com", first);
    }

    [Fact]
    public void Media_domains_are_recognized()
    {
        Assert.True(OfficialSiteCatalog.IsMediaDomain("www.lemonde.fr"));
        Assert.True(OfficialSiteCatalog.IsMediaDomain("lefigaro.fr"));
        Assert.False(OfficialSiteCatalog.IsMediaDomain("example.com"));
    }

    [Fact]
    public void Docs_hosts_are_recognized()
    {
        Assert.True(OfficialSiteCatalog.IsDocsHost("docs.python.org"));
        Assert.True(OfficialSiteCatalog.IsDocsHost("learn.microsoft.com"));
        Assert.False(OfficialSiteCatalog.IsDocsHost("python.org"));
    }

    [Fact]
    public void HostMatchesDomain_matches_root_and_subdomains()
    {
        Assert.Equal("microsoft.com", OfficialSiteCatalog.HostMatchesDomain("microsoft.com", "microsoft.com"));
        Assert.Equal("microsoft.com", OfficialSiteCatalog.HostMatchesDomain("www.microsoft.com", "microsoft.com"));
        Assert.Equal(string.Empty, OfficialSiteCatalog.HostMatchesDomain("microsoft.fr", "microsoft.com"));
    }
}
