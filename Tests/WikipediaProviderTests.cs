using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class WikipediaProviderTests
{
    private const string Json = """
        {"query":{"searchinfo":{"totalhits":100},"search":[
          {"title":"Transformer (machine learning)","snippet":"<span class=\"searchmatch\">Transformer</span> is a deep learning architecture"},
          {"title":"Transformer","snippet":"Disambiguation page"}
        ]}}
        """;

    [Fact]
    public void Parses_wikipedia_json()
    {
        var results = WikipediaSearchProvider.ParseResults(Json, 10);

        Assert.Equal(2, results.Count);
        Assert.Equal("Transformer (machine learning)", results[0].Title);
        Assert.Equal("https://fr.wikipedia.org/wiki/Transformer_(machine_learning)", results[0].Url);
    }

    [Fact]
    public void Strips_html_from_snippets()
    {
        var results = WikipediaSearchProvider.ParseResults(Json, 10);

        Assert.DoesNotContain("<span", results[0].Snippet);
        Assert.Contains("Transformer", results[0].Snippet);
    }

    [Fact]
    public void Respects_max_results()
    {
        var results = WikipediaSearchProvider.ParseResults(Json, 1);

        Assert.Single(results);
    }

    [Fact]
    public void Invalid_json_returns_empty()
    {
        var results = WikipediaSearchProvider.ParseResults("not json", 10);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_uses_wiki_api()
    {
        var handler = StubHttpHandler.For(Json);
        var provider = new WikipediaSearchProvider(new HttpClient(handler), NullLogger<WikipediaSearchProvider>.Instance);

        var results = await provider.SearchAsync(new SearchRequest { Query = "transformer", Language = "fr" });

        Assert.Equal(2, results.Count);
        Assert.Contains("wikipedia.org/w/api.php", handler.Requests[0].RequestUri!.ToString());
    }
}
