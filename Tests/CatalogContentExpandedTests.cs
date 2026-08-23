using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class CatalogContentExpandedTests
{
    public static IEnumerable<object[]> AllDomainKeys
    {
        get
        {
            foreach (var key in OfficialSiteCatalog.DomainKeys)
                yield return new object[] { key };
        }
    }

    public static IEnumerable<object[]> AllRepoKeys
    {
        get
        {
            foreach (var key in OfficialSiteCatalog.RepoKeys)
                yield return new object[] { key };
        }
    }

    public static IEnumerable<object[]> AllChannelKeys
    {
        get
        {
            foreach (var key in OfficialSiteCatalog.ChannelKeys)
                yield return new object[] { key };
        }
    }

    public static IEnumerable<object[]> AllSchoolKeys
    {
        get
        {
            foreach (var key in OfficialSiteCatalog.SchoolKeys)
                yield return new object[] { key };
        }
    }

    [Theory]
    [MemberData(nameof(AllDomainKeys))]
    public void Every_domain_key_resolves(string key)
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialDomain(key, out var domain));
        Assert.False(string.IsNullOrWhiteSpace(domain));
    }

    [Theory]
    [MemberData(nameof(AllRepoKeys))]
    public void Every_repo_key_resolves(string key)
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialRepo(key, out var repo));
        Assert.False(string.IsNullOrWhiteSpace(repo));
    }

    [Theory]
    [MemberData(nameof(AllChannelKeys))]
    public void Every_channel_key_resolves(string key)
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialChannel(key, out var url));
        Assert.StartsWith("https://", url);
    }

    [Theory]
    [MemberData(nameof(AllSchoolKeys))]
    public void Every_school_key_resolves(string key)
    {
        Assert.True(OfficialSiteCatalog.TryGetSchoolUrl(key, out var url));
        Assert.StartsWith("https://", url);
    }

    [Fact]
    public void AllDomains_is_distinct_and_non_empty()
    {
        Assert.NotEmpty(OfficialSiteCatalog.AllDomains);
        Assert.Equal(
            OfficialSiteCatalog.AllDomains.Count,
            OfficialSiteCatalog.AllDomains.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_all_domain_maps_back_to_a_key()
    {
        foreach (var domain in OfficialSiteCatalog.AllDomains)
        {
            Assert.Contains(
                OfficialSiteCatalog.DomainKeys,
                key => OfficialSiteCatalog.TryGetOfficialDomain(key, out var d)
                       && string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("microsoft")]
    [InlineData("apple")]
    [InlineData("google")]
    [InlineData("github")]
    [InlineData("ollama")]
    [InlineData("youtube")]
    [InlineData("wikipedia")]
    [InlineData("python")]
    [InlineData("lemonde")]
    [InlineData("parcoursup")]
    [InlineData("notion")]
    [InlineData("spotify")]
    public void Well_known_domains_resolve(string key)
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialDomain(key, out var domain));
        Assert.False(string.IsNullOrWhiteSpace(domain));
    }

    [Theory]
    [InlineData("docs.python.org")]
    [InlineData("docs.microsoft.com")]
    [InlineData("learn.microsoft.com")]
    [InlineData("matplotlib.readthedocs.io")]
    [InlineData("microsoft.github.io")]
    public void Well_known_docs_hosts_are_detected(string host)
    {
        Assert.True(OfficialSiteCatalog.IsDocsHost(host));
    }

    [Theory]
    [InlineData("lemonde.fr")]
    [InlineData("theverge.com")]
    [InlineData("techcrunch.com")]
    [InlineData("franceinfo.fr")]
    [InlineData("bbc.com")]
    [InlineData("reuters.com")]
    public void Well_known_media_domains_are_detected(string host)
    {
        Assert.True(OfficialSiteCatalog.IsMediaDomain(host));
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("microsoft")]
    [InlineData("linux")]
    [InlineData("react")]
    [InlineData("tesseract")]
    [InlineData("vllm")]
    [InlineData("open-webui")]
    [InlineData("langflow")]
    public void Well_known_repos_resolve(string key)
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialRepo(key, out var repo));
        Assert.Contains("/", repo);
        Assert.False(string.IsNullOrWhiteSpace(repo));
    }

    [Theory]
    [InlineData("mrbeast")]
    [InlineData("fireship")]
    [InlineData("veritasium")]
    [InlineData("3blue1brown")]
    [InlineData("mkbhd")]
    [InlineData("two minute papers")]
    [InlineData("the verge")]
    [InlineData("kurzgesagt")]
    [InlineData("bfmtv")]
    [InlineData("openai")]
    [InlineData("linus tech tips")]
    [InlineData("lex fridman")]
    public void Well_known_channels_resolve(string key)
    {
        Assert.True(OfficialSiteCatalog.TryGetOfficialChannel(key, out var url));
        Assert.StartsWith("https://", url);
    }

    [Theory]
    [InlineData("polytechnique")]
    [InlineData("ecole polytechnique")]
    [InlineData("centrale paris")]
    [InlineData("mines paris")]
    [InlineData("hec paris")]
    [InlineData("sciences po")]
    [InlineData("insa toulouse")]
    [InlineData("cpge")]
    [InlineData("académie de bordeaux")]
    [InlineData("crous")]
    public void Well_known_schools_resolve(string key)
    {
        Assert.True(OfficialSiteCatalog.TryGetSchoolUrl(key, out var url));
        Assert.StartsWith("https://", url);
    }
}
