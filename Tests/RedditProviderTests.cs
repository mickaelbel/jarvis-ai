using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class RedditProviderTests
{
    private const string Json = """
        {"kind":"Listing","data":{"after":"t3_xyz","children":[
          {"kind":"t3","data":{"title":"Best phone 2026?","permalink":"/r/Android/comments/abc/best_phone_2026/","subreddit_name_prefixed":"r/Android","score":1200,"num_comments":340,"created_utc":1700000000,"selftext":"I think it's the Pixel 10 honestly"}},
          {"kind":"t3","data":{"title":"Weekly tech thread","permalink":"/r/Android/comments/def/weekly/","subreddit_name_prefixed":"r/Android","score":45,"num_comments":12,"created_utc":1700001000,"selftext":""}}
        ]}}
        """;

    [Fact]
    public void Parses_reddit_posts()
    {
        var results = RedditSearchProvider.ParseResults(Json, 10);

        Assert.Equal(2, results.Count);
        Assert.Equal("Best phone 2026?", results[0].Title);
        Assert.Equal("https://www.reddit.com/r/Android/comments/abc/best_phone_2026/", results[0].Url);
        Assert.Equal("r/Android", results[0].Source);
    }

    [Fact]
    public void Snippet_uses_selftext_when_present()
    {
        var results = RedditSearchProvider.ParseResults(Json, 10);

        Assert.Contains("Pixel 10", results[0].Snippet);
    }

    [Fact]
    public void Snippet_uses_stats_when_no_selftext()
    {
        var results = RedditSearchProvider.ParseResults(Json, 10);

        Assert.Contains("commentaires", results[1].Snippet);
    }

    [Fact]
    public void Parses_creation_date()
    {
        var results = RedditSearchProvider.ParseResults(Json, 10);

        Assert.NotNull(results[0].PublishedAt);
    }

    [Fact]
    public async Task Search_hits_reddit_json()
    {
        var handler = StubHttpHandler.For(Json);
        var provider = new RedditSearchProvider(new HttpClient(handler), NullLogger<RedditSearchProvider>.Instance);

        var results = await provider.SearchAsync(new SearchRequest { Query = "best phone reddit" });

        Assert.Equal(2, results.Count);
        Assert.Contains("reddit.com/search.json", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public void Only_handles_reddit_queries()
    {
        var provider = new RedditSearchProvider(new HttpClient(StubHttpHandler.For(Json)), NullLogger<RedditSearchProvider>.Instance);

        Assert.True(provider.CanHandle(new SearchRequest { Query = "best phone reddit" }));
        Assert.False(provider.CanHandle(new SearchRequest { Query = "best phone" }));
    }
}
