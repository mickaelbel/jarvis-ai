using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class CitationBuilderTests
{
    [Fact]
    public void Formats_citation_with_source_and_url()
    {
        var result = TestResults.Result("Breaking News", "https://lemonde.fr/art", "news", published: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
        result.Confidence = 0.85;

        var citation = CitationBuilder.Format(result);

        Assert.Contains("Breaking News", citation);
        Assert.Contains("https://lemonde.fr/art", citation);
        Assert.Contains("2026-01-15", citation);
        Assert.Contains("85%", citation);
    }

    [Fact]
    public void Omits_date_when_absent()
    {
        var result = TestResults.Result("No Date", "https://example.com", "bing");
        result.Confidence = 0.5;

        var citation = CitationBuilder.Format(result);

        Assert.DoesNotContain("confiance", citation.Split("—").First());
        Assert.Contains("confiance", citation);
    }

    [Fact]
    public void Includes_author_when_present()
    {
        var result = TestResults.Result("Repo", "https://github.com/x/y", "github", author: "x");
        result.Confidence = 0.9;

        var citation = CitationBuilder.Format(result);

        Assert.Contains("Source: x", citation);
    }

    [Fact]
    public void Formats_all_results()
    {
        var results = new[]
        {
            TestResults.Result("A", "https://a.com", "bing"),
            TestResults.Result("B", "https://b.com", "wikipedia")
        };

        var citations = CitationBuilder.FormatAll(results);

        Assert.Equal(2, citations.Count);
        Assert.All(citations, c => Assert.Contains("confiance", c));
    }

    [Fact]
    public void Plain_list_is_newline_joined()
    {
        var results = new[] { TestResults.Result("A", "https://a.com", "bing") };

        var output = CitationBuilder.FormatPlainList(results);

        Assert.Equal(CitationBuilder.Format(results[0]), output.Trim());
    }
}
