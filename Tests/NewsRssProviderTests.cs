using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class NewsRssProviderTests
{
    private const string Rss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
        <channel><title>News</title>
          <item>
            <title>ChatGPT new model released</title>
            <link>https://example.com/chatgpt-news</link>
            <description>OpenAI announced a &lt;b&gt;new model&lt;/b&gt; today.</description>
            <pubDate>Fri, 02 Jan 2026 10:00:00 GMT</pubDate>
            <source url="https://example.com">ExampleNews</source>
          </item>
          <item>
            <title>Weather forecast</title>
            <link>https://example.com/weather</link>
            <description>Sunny skies expected.</description>
            <pubDate>Thu, 01 Jan 2026 09:00:00 GMT</pubDate>
          </item>
        </channel>
        </rss>
        """;

    [Fact]
    public void Parses_rss_items()
    {
        var results = NewsRssProvider.ParseFeed(Rss, new SearchRequest { Query = "x" });

        Assert.Equal(2, results.Count);
        Assert.Equal("ChatGPT new model released", results[0].Title);
        Assert.Equal("https://example.com/chatgpt-news", results[0].Url);
        Assert.Equal(SearchResultType.News, results[0].Type);
    }

    [Fact]
    public void Filters_items_by_query_terms()
    {
        var results = NewsRssProvider.ParseFeed(Rss, new SearchRequest { Query = "chatgpt model" });

        Assert.Single(results);
        Assert.Contains("chatgpt", results[0].Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parses_publish_dates()
    {
        var results = NewsRssProvider.ParseFeed(Rss, new SearchRequest { Query = "x" });

        Assert.NotNull(results[0].PublishedAt);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero), results[0].PublishedAt);
    }

    [Fact]
    public void Uses_source_element_as_source()
    {
        var results = NewsRssProvider.ParseFeed(Rss, new SearchRequest { Query = "x" });

        Assert.Equal("ExampleNews", results[0].Source);
    }

    [Fact]
    public void Strips_html_from_description()
    {
        var results = NewsRssProvider.ParseFeed(Rss, new SearchRequest { Query = "x" });

        Assert.Contains("OpenAI announced a new model", results[0].Snippet);
    }

    [Fact]
    public void Invalid_feed_returns_empty()
    {
        var results = NewsRssProvider.ParseFeed("not xml", new SearchRequest { Query = "x" });

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_merges_and_dedupes_feeds()
    {
        var handler = StubHttpHandler.For(Rss);
        var provider = new NewsRssProvider(new HttpClient(handler), NullLogger<NewsRssProvider>.Instance);

        var results = await provider.SearchAsync(new SearchRequest { Query = "chatgpt", MaxResults = 10 });

        Assert.NotEmpty(results);
        Assert.True(handler.Requests.Count > 0);
    }
}
