using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class ArxivProviderTests
{
    private const string Atom = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry>
            <title>Attention Is All You Need</title>
            <id>http://arxiv.org/abs/1706.03762v5</id>
            <summary>We propose a new network architecture, the Transformer, based on attention mechanisms.</summary>
            <published>2017-06-12T00:00:00Z</published>
            <author><name>Ashish Vaswani</name></author>
          </entry>
          <entry>
            <title>BERT: Pre-training of Deep Bidirectional Transformers</title>
            <id>http://arxiv.org/abs/1810.04805v2</id>
            <summary>Introduces BERT.</summary>
            <published>2018-10-11T00:00:00Z</published>
            <author><name>Jacob Devlin</name></author>
          </entry>
        </feed>
        """;

    [Fact]
    public void Parses_atom_entries()
    {
        var results = ArxivSearchProvider.ParseResults(Atom, 10);

        Assert.Equal(2, results.Count);
        Assert.Equal("Attention Is All You Need", results[0].Title);
        Assert.Equal("https://arxiv.org/abs/1706.03762v5", results[0].Url);
        Assert.Equal(SearchResultType.Academic, results[0].Type);
    }

    [Fact]
    public void Upgrades_http_to_https()
    {
        var results = ArxivSearchProvider.ParseResults(Atom, 10);

        Assert.All(results, r => Assert.StartsWith("https://", r.Url));
    }

    [Fact]
    public void Parses_authors_and_date()
    {
        var results = ArxivSearchProvider.ParseResults(Atom, 10);

        Assert.Equal("Ashish Vaswani", results[0].Author);
        Assert.Equal(new DateTimeOffset(2017, 6, 12, 0, 0, 0, TimeSpan.Zero), results[0].PublishedAt);
    }

    [Fact]
    public void Truncates_long_summaries()
    {
        var results = ArxivSearchProvider.ParseResults(Atom, 10);

        Assert.All(results, r => Assert.True(r.Snippet.Length <= 303));
    }

    [Fact]
    public async Task Search_queries_arxiv_api()
    {
        var handler = StubHttpHandler.For(Atom);
        var provider = new ArxivSearchProvider(new HttpClient(handler), NullLogger<ArxivSearchProvider>.Instance);

        var results = await provider.SearchAsync(new SearchRequest { Query = "attention", Type = SearchResultType.Academic });

        Assert.Equal(2, results.Count);
        Assert.Contains("export.arxiv.org", handler.Requests[0].RequestUri!.ToString());
    }
}
