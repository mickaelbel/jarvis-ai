using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class BingProviderTests
{
    private const string Html = """
        <html><body>
        <ol id="b_results">
        <li class="b_algo">
          <h2><a href="https://www.bing.com/ck/a?a=1&amp;u=aHR0cHM6Ly9leGFtcGxlLmNvbS9wYWdl&amp;ntb=1">Example Result</a></h2>
          <p class="b_lineclamp4">A short snippet describing the page.</p>
        </li>
        <li class="b_algo">
          <h2><a href="https://openai.com/">OpenAI Official</a></h2>
          <p class="b_lineclamp4">ChatGPT by OpenAI.</p>
        </li>
        </ol>
        </body></html>
        """;

    [Fact]
    public void Parses_bing_result_blocks()
    {
        var results = BingSearchProvider.ParseResults(Html, 10);

        Assert.Equal(2, results.Count);
        Assert.Equal("Example Result", results[0].Title);
        Assert.Equal("https://example.com/page", results[0].Url);
    }

    [Fact]
    public void Decodes_base64_redirect_links()
    {
        var results = BingSearchProvider.ParseResults(Html, 10);

        Assert.Equal("https://example.com/page", results[0].Url);
    }

    [Fact]
    public void Keeps_direct_links_untouched()
    {
        var results = BingSearchProvider.ParseResults(Html, 10);

        Assert.Equal("https://openai.com/", results[1].Url);
    }

    [Fact]
    public void Respects_max_results()
    {
        var results = BingSearchProvider.ParseResults(Html, 1);

        Assert.Single(results);
    }

    [Fact]
    public async Task Search_hits_bing_endpoint()
    {
        var handler = StubHttpHandler.For(Html);
        var provider = new BingSearchProvider(new HttpClient(handler), NullLogger<BingSearchProvider>.Instance);

        var results = await provider.SearchAsync(new SearchRequest { Query = "test", Language = "fr" });

        Assert.Equal(2, results.Count);
        Assert.Contains("www.bing.com/search", handler.Requests[0].RequestUri!.ToString());
    }
}
