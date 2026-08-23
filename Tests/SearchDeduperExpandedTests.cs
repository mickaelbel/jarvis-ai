using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class SearchDeduperExpandedTests
{
    [Theory]
    [InlineData("https://www.Example.com/path/", "https://example.com/path")]
    [InlineData("https://example.com/path", "https://example.com/path/")]
    [InlineData("https://example.com/PATH", "https://example.com/path")]
    [InlineData("http://example.com/path", "https://example.com/path")]
    [InlineData("https://example.com/?a=1&b=2", "https://example.com/?b=2&a=1")]
    [InlineData("https://example.com/p?utm_source=x&utm_medium=y&keep=1", "https://example.com/p?keep=1")]
    [InlineData("https://example.com/p?gclid=123&utm_campaign=c&id=5", "https://example.com/p?id=5")]
    public void CanonicalKey_merges_equivalent_urls(string a, string b)
    {
        Assert.Equal(SearchResultDeduper.CanonicalKey(a), SearchResultDeduper.CanonicalKey(b));
    }

    [Theory]
    [InlineData("https://example.com/p?a=1", "https://example.com/p?a=2")]
    [InlineData("https://example.com/p", "https://example.com/q")]
    [InlineData("https://a.example.com/p", "https://b.example.com/p")]
    public void CanonicalKey_keeps_different_urls_distinct(string a, string b)
    {
        Assert.NotEqual(SearchResultDeduper.CanonicalKey(a), SearchResultDeduper.CanonicalKey(b));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://files.example.com/x")]
    [InlineData("")]
    public void CanonicalKey_rejects_invalid_urls(string url)
    {
        Assert.Null(SearchResultDeduper.CanonicalKey(url));
    }

    [Theory]
    [InlineData("https://www.Example.com/path", "example.com")]
    [InlineData("https://example.com/path", "example.com")]
    [InlineData("http://sub.example.com/path", "sub.example.com")]
    public void CanonicalDomain_normalizes_host(string url, string expected)
    {
        Assert.Equal(expected, SearchResultDeduper.CanonicalDomain(url));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("")]
    public void CanonicalDomain_rejects_invalid_urls(string url)
    {
        Assert.Equal(string.Empty, SearchResultDeduper.CanonicalDomain(url));
    }

    [Fact]
    public void Dedupe_keeps_higher_confidence_result()
    {
        var deduper = new SearchResultDeduper();
        var lower = TestResults.Result("Low", "https://x.com/a");
        lower.Confidence = 0.3;
        var higher = TestResults.Result("High", "https://www.x.com/a/");
        higher.Confidence = 0.9;

        var kept = deduper.Dedupe(new[] { lower, higher });

        Assert.Single(kept);
        Assert.Equal("High", kept[0].Title);
    }

    [Fact]
    public void Dedupe_keeps_official_over_plain_at_equal_confidence()
    {
        var deduper = new SearchResultDeduper();
        var plain = TestResults.Result("Plain", "https://x.com/a");
        plain.Confidence = 0.5;
        var official = TestResults.Result("Official", "https://www.x.com/a/");
        official.Confidence = 0.5;
        official.IsOfficial = true;

        var kept = deduper.Dedupe(new[] { plain, official });

        Assert.Single(kept);
        Assert.Equal("Official", kept[0].Title);
    }

    [Fact]
    public void Dedupe_keeps_verified_over_plain_at_equal_confidence()
    {
        var deduper = new SearchResultDeduper();
        var plain = TestResults.Result("Plain", "https://x.com/a");
        plain.Confidence = 0.5;
        var verified = TestResults.Result("Verified", "https://www.x.com/a/");
        verified.Confidence = 0.5;
        verified.IsVerified = true;

        var kept = deduper.Dedupe(new[] { plain, verified });

        Assert.Single(kept);
        Assert.Equal("Verified", kept[0].Title);
    }

    [Fact]
    public void Dedupe_keeps_distinct_urls()
    {
        var deduper = new SearchResultDeduper();
        var a = TestResults.Result("A", "https://x.com/a");
        var b = TestResults.Result("B", "https://y.com/b");

        var kept = deduper.Dedupe(new[] { a, b });

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Dedupe_skips_invalid_urls()
    {
        var deduper = new SearchResultDeduper();
        var valid = TestResults.Result("A", "https://x.com/a");
        var invalid = TestResults.Result("B", "not a url");

        var kept = deduper.Dedupe(new[] { valid, invalid });

        Assert.Single(kept);
        Assert.Equal("A", kept[0].Title);
    }

    [Fact]
    public void Dedupe_removes_tracking_parameter_variants()
    {
        var deduper = new SearchResultDeduper();
        var withUtm = TestResults.Result("A", "https://x.com/p?utm_source=newsletter");
        var bare = TestResults.Result("B", "https://x.com/p");

        var kept = deduper.Dedupe(new[] { withUtm, bare });

        Assert.Single(kept);
    }
}
