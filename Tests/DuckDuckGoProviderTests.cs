using System.Net;
using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class DuckDuckGoProviderTests
{
    private const string Html = """
        <html><body>
        <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fpage&amp;rut=abc123">Example Page Title</a>
        <a class="result__snippet" rel="nofollow" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fpage">This is the snippet text for the result.</a>
        <a rel="nofollow" class="result__a" href="https://github.com/ollama/ollama">Ollama on GitHub</a>
        <a class="result__snippet" rel="nofollow">GitHub repository page.</a>
        </body></html>
        """;

    [Fact]
    public void Parses_titles_urls_and_snippets()
    {
        var results = DuckDuckGoSearchProvider.ParseResults(Html, 10);

        Assert.Equal(2, results.Count);
        Assert.Equal("Example Page Title", results[0].Title);
        Assert.Equal("https://example.com/page", results[0].Url);
        Assert.Contains("snippet", results[0].Snippet);
        Assert.Equal("example.com", results[0].Source);
    }

    [Fact]
    public void Decodes_redirect_urls()
    {
        var results = DuckDuckGoSearchProvider.ParseResults(Html, 10);

        Assert.Equal("https://github.com/ollama/ollama", results[1].Url);
    }

    [Fact]
    public void Respects_max_results()
    {
        var results = DuckDuckGoSearchProvider.ParseResults(Html, 1);

        Assert.Single(results);
    }

    [Fact]
    public void Empty_html_returns_no_results()
    {
        var results = DuckDuckGoSearchProvider.ParseResults("<html></html>", 10);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_uses_duckduckgo_endpoint()
    {
        var handler = StubHttpHandler.For(Html);
        var provider = new DuckDuckGoSearchProvider(new HttpClient(handler), NullLogger<DuckDuckGoSearchProvider>.Instance);

        var results = await provider.SearchAsync(new SearchRequest { Query = "test" });

        Assert.Equal(2, results.Count);
        Assert.Contains("html.duckduckgo.com", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Search_error_returns_no_results()
    {
        var handler = StubHttpHandler.For("<html></html>", HttpStatusCode.InternalServerError);
        var provider = new DuckDuckGoSearchProvider(new HttpClient(handler), NullLogger<DuckDuckGoSearchProvider>.Instance);

        var results = await provider.SearchAsync(new SearchRequest { Query = "test" });

        Assert.Empty(results);
    }
}
