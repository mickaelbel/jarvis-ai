using JarvisAI.Application.Search;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class GitHubProviderTests
{
    private const string Json = """
        {"total_count":2,"items":[
          {"full_name":"ollama/ollama","html_url":"https://github.com/ollama/ollama","description":"Get up and running with Llama","stargazers_count":95000,"owner":{"login":"ollama"},"language":"Go","updated_at":"2026-01-01T00:00:00Z","fork":false},
          {"full_name":"someone/ollama-fork","html_url":"https://github.com/someone/ollama-fork","description":"a fork","stargazers_count":10,"owner":{"login":"someone"},"language":"Go","updated_at":"2025-01-01T00:00:00Z","fork":true}
        ]}
        """;

    [Fact]
    public void Parses_repositories_and_metadata()
    {
        var results = GitHubSearchProvider.ParseResults(Json, 10);

        Assert.Single(results);
        Assert.Equal("ollama/ollama", results[0].Title);
        Assert.Equal("https://github.com/ollama/ollama", results[0].Url);
        Assert.Equal("ollama", results[0].Author);
        Assert.Equal(SearchResultType.Repository, results[0].Type);
        Assert.Contains("stars", results[0].Snippet);
    }

    [Fact]
    public void Excludes_forks_entirely()
    {
        var results = GitHubSearchProvider.ParseResults(Json, 10);

        Assert.DoesNotContain(results, r => r.Url.Contains("ollama-fork", StringComparison.Ordinal));
    }

    [Fact]
    public void Star_count_sets_extra_score()
    {
        var results = GitHubSearchProvider.ParseResults(Json, 10);

        Assert.NotNull(results[0].ExtraScore);
        Assert.InRange(results[0].ExtraScore!.Value, 0.0, 1.0);
    }

    [Fact]
    public void Parses_updated_date()
    {
        var results = GitHubSearchProvider.ParseResults(Json, 10);

        Assert.NotNull(results[0].PublishedAt);
    }

    [Fact]
    public async Task Search_hits_github_api()
    {
        var handler = StubHttpHandler.For(Json);
        var provider = new GitHubSearchProvider(new HttpClient(handler), NullLogger<GitHubSearchProvider>.Instance);

        var results = await provider.SearchAsync(new SearchRequest { Query = "ollama", Type = SearchResultType.Repository });

        Assert.Single(results);
        Assert.Contains("api.github.com/search/repositories", handler.Requests[0].RequestUri!.ToString());
    }
}
