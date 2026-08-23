using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class SearchDedupTests
{
    private readonly SearchResultDeduper _deduper = new();

    [Fact]
    public void Removes_duplicate_urls_keeping_highest_confidence()
    {
        var low = TestResults.Result("Title", "https://example.com/page");
        low.Confidence = 0.4;
        var high = TestResults.Result("Title", "https://example.com/page");
        high.Confidence = 0.9;

        var result = _deduper.Dedupe(new[] { low, high });

        Assert.Single(result);
        Assert.Equal(0.9, result[0].Confidence);
    }

    [Fact]
    public void Treats_tracking_params_as_duplicates()
    {
        var a = TestResults.Result("A", "https://site.com/article?utm_source=x&id=1");
        var b = TestResults.Result("B", "https://site.com/article?id=1&utm_medium=mail");

        var result = _deduper.Dedupe(new[] { a, b });

        Assert.Single(result);
    }

    [Fact]
    public void Normalizes_www_prefix()
    {
        var a = TestResults.Result("A", "https://www.example.com/page");
        var b = TestResults.Result("B", "https://example.com/page");

        var result = _deduper.Dedupe(new[] { a, b });

        Assert.Single(result);
    }

    [Fact]
    public void Keeps_distinct_pages()
    {
        var a = TestResults.Result("A", "https://example.com/a");
        var b = TestResults.Result("B", "https://example.com/b");

        var result = _deduper.Dedupe(new[] { a, b });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Ignores_scheme_for_key_but_preserves_first()
    {
        var a = TestResults.Result("A", "https://example.com/x");
        var b = TestResults.Result("B", "http://example.com/x");

        var result = _deduper.Dedupe(new[] { a, b });

        Assert.Single(result);
    }

    [Theory]
    [InlineData("https://github.com/ollama/ollama", "github.com/ollama/ollama")]
    [InlineData("https://www.youtube.com/watch?v=abc123&t=30", "youtube.com/watch?t=30&v=abc123")]
    [InlineData("https://example.com/?a=1&b=2", "example.com/?a=1&b=2")]
    public void Canonical_key_normalizes(string url, string expected)
    {
        var key = SearchResultDeduper.CanonicalKey(url);

        Assert.NotNull(key);
        Assert.Equal(expected, key);
    }

    [Fact]
    public void Canonical_domain_strips_www()
    {
        Assert.Equal("github.com", SearchResultDeduper.CanonicalDomain("https://www.github.com/foo"));
    }

    [Fact]
    public void Invalid_urls_are_skipped()
    {
        var result = _deduper.Dedupe(new[] { TestResults.Result("A", "not a url") });

        Assert.Empty(result);
    }
}
