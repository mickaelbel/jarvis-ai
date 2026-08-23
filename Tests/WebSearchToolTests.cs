using JarvisAI.Application.Agents;
using JarvisAI.Application.Search;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class WebSearchToolTests
{
    private static (WebSearchTool tool, FakeLinkVerifier verifier, List<string> opened) CreateTool(
        IReadOnlyList<IWebSearchProvider>? providers = null)
    {
        var verifier = new FakeLinkVerifier();
        var service = new WebSearchService(
            providers ?? new IWebSearchProvider[] { },
            verifier,
            new SearchCache(),
            new OfficialSiteDetector(),
            new FakeSiteDetector(),
            NullLogger<WebSearchService>.Instance);
        var opened = new List<string>();
        var tool = new WebSearchTool(service, NullLogger<WebSearchTool>.Instance, opened.Add);
        return (tool, verifier, opened);
    }

    private static IReadOnlyDictionary<string, string> Params(params string[] kv)
    {
        var dict = new Dictionary<string, string>();
        for (var i = 0; i < kv.Length; i += 2)
            dict[kv[i]] = kv[i + 1];
        return dict;
    }

    [Fact]
    public async Task Search_formats_results_with_citations()
    {
        var (tool, _, _) = CreateTool(new IWebSearchProvider[]
        {
            new StubSearchProvider("bing", TestResults.Result("Result One", "https://sample-article.example.net/1", "bing"))
        });

        var result = await tool.ExecuteAsync(new AgentContext("x"), Params("action", "search", "query", "test"));

        Assert.True(result.Success);
        Assert.Contains("Result One", result.Output);
        Assert.Contains("https://sample-article.example.net/1", result.Output);
        Assert.Contains("confiance", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_returns_empty_message_when_no_results()
    {
        var (tool, _, _) = CreateTool(new IWebSearchProvider[] { new StubSearchProvider("bing") });

        var result = await tool.ExecuteAsync(new AgentContext("x"), Params("action", "search", "query", "nothing"));

        Assert.True(result.Success);
        Assert.Contains("Aucun résultat", result.Output);
    }

    [Fact]
    public async Task Search_requires_query()
    {
        var (tool, _, _) = CreateTool();

        var result = await tool.ExecuteAsync(new AgentContext("x"), Params("action", "search"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Open_resolves_official_site_and_opens_browser()
    {
        var (tool, _, opened) = CreateTool();

        var result = await tool.ExecuteAsync(new AgentContext("x"), Params("action", "open", "query", "ouvre le site Samsung"));

        Assert.True(result.Success);
        Assert.Single(opened);
        Assert.Contains("samsung.com", opened[0]);
    }

    [Fact]
    public async Task Open_resolves_official_github_repo()
    {
        var (tool, _, opened) = CreateTool();

        var result = await tool.ExecuteAsync(new AgentContext("x"), Params("action", "open", "query", "ouvre le github d'Ollama"));

        Assert.True(result.Success);
        Assert.Equal("https://github.com/ollama/ollama", opened[0]);
    }

    [Fact]
    public async Task Open_refuses_unverified_link()
    {
        var (tool, verifier, opened) = CreateTool();
        verifier.Rejected.Add("https://samsung.com");

        var result = await tool.ExecuteAsync(new AgentContext("x"), Params("action", "open", "query", "ouvre le site Samsung"));

        Assert.False(result.Success);
        Assert.Empty(opened);
    }

    [Fact]
    public async Task Resolve_official_reports_match()
    {
        var (tool, _, _) = CreateTool();

        var result = await tool.ExecuteAsync(new AgentContext("x"), Params("action", "resolve_official", "query", "le github d'Ollama"));

        Assert.True(result.Success);
        Assert.Contains("https://github.com/ollama/ollama", result.Output);
    }

    [Fact]
    public async Task Verify_link_validates_url()
    {
        var (tool, verifier, _) = CreateTool();
        verifier.Valid = true;

        var result = await tool.ExecuteAsync(new AgentContext("x"), Params("action", "verify_link", "url", "https://example.com"));

        Assert.True(result.Success);
        Assert.Contains("OK", result.Output);
    }

    [Fact]
    public async Task Unknown_action_fails_gracefully()
    {
        var (tool, _, _) = CreateTool();

        var result = await tool.ExecuteAsync(new AgentContext("x"), Params("action", "bogus"));

        Assert.False(result.Success);
    }
}
