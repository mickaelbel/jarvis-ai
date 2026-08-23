using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class StackOverflowProviderTests
{
    private const string Json = """
        {"items":[
          {"title":"How to parse JSON in &lt;b&gt;C#&lt;/b&gt;","link":"https://stackoverflow.com/questions/1/how-to-parse-json-in-c","score":120,"is_answered":true,"creation_date":1600000000,"tags":["c#","json"]},
          {"title":"Async await deadlock","link":"https://stackoverflow.com/questions/2/async-await-deadlock","score":55,"is_answered":false,"creation_date":1600001000,"tags":["async"]}
        ]}
        """;

    [Fact]
    public void Parses_questions()
    {
        var results = StackOverflowSearchProvider.ParseResults(Json, 10);

        Assert.Equal(2, results.Count);
        Assert.Equal("How to parse JSON in C#", results[0].Title);
        Assert.Equal("https://stackoverflow.com/questions/1/how-to-parse-json-in-c", results[0].Url);
    }

    [Fact]
    public void Marks_answered_questions()
    {
        var results = StackOverflowSearchProvider.ParseResults(Json, 10);

        Assert.Contains("[Répondu]", results[0].Snippet);
        Assert.Contains("[Sans réponse]", results[1].Snippet);
    }

    [Fact]
    public void Includes_tags_in_snippet()
    {
        var results = StackOverflowSearchProvider.ParseResults(Json, 10);

        Assert.Contains("c#", results[0].Snippet);
    }

    [Fact]
    public async Task Search_hits_stackexchange_api()
    {
        var handler = StubHttpHandler.For(Json);
        var provider = new StackOverflowSearchProvider(new HttpClient(handler), NullLogger<StackOverflowSearchProvider>.Instance);

        var results = await provider.SearchAsync(new SearchRequest { Query = "parse json c#" });

        Assert.Equal(2, results.Count);
        Assert.Contains("api.stackexchange.com", handler.Requests[0].RequestUri!.ToString());
    }
}
