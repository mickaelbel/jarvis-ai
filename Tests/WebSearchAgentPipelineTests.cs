using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Search;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Events.Agents;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Memory;
using JarvisAI.Infrastructure.Search;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

/// <summary>
/// End-to-end pipeline tests: the real path used by the agent
/// (AIService / ToolExecutor -> ToolRegistry -> WebSearchTool -> WebSearchService),
/// backed by stub providers and a stub link verifier so no network is hit.
/// </summary>
public sealed class WebSearchAgentPipelineTests
{
    private static MemoryService CreateMemoryService()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        return new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
    }

    private static WebSearchService CreateSearchService()
        => new(
            new IWebSearchProvider[]
            {
                new StubSearchProvider("news",
                    TestResults.Result("Sample News Article", "https://sample-news.example.org/2026/08/ai", "news"),
                    TestResults.Result("Different Angle", "https://sample-news.example.org/2026/08/analysis", "news")),
                new StubSearchProvider("duckduckgo",
                    TestResults.Result("Sample News Article", "https://sample-news.example.org/2026/08/ai", "duckduckgo"),
                    TestResults.Result("Another Source", "https://another-source.example.net/story", "duckduckgo")),
                new StubSearchProvider("bing",
                    TestResults.Result("Sample News Article", "https://sample-news.example.org/2026/08/ai", "bing"))
            },
            new FakeLinkVerifier(),
            new SearchCache(),
            new OfficialSiteDetector(),
            new FakeSiteDetector(),
            NullLogger<WebSearchService>.Instance);

    private static (ToolRegistry registry, InMemoryEventBus eventBus, List<string> opened) CreateSystem()
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var opened = new List<string>();
        var service = CreateSearchService();
        registry.Register(new WebSearchTool(service, NullLogger<WebSearchTool>.Instance, opened.Add));
        return (registry, eventBus, opened);
    }

    private static AgentContext Context(Dictionary<string, string> args)
        => new("web search", source: "test", new Dictionary<string, object> { ["arguments"] = args });

    private static Dictionary<string, string> Args(params string[] keyValues)
    {
        var dict = new Dictionary<string, string>();
        for (var i = 0; i < keyValues.Length; i += 2)
            dict[keyValues[i]] = keyValues[i + 1];
        return dict;
    }

    // ─── TEST 1: Full agent pipeline ────────────────────────────────────────

    [Fact]
    public async Task Agent_executes_web_search_tool_call_end_to_end()
    {
        var (registry, eventBus, _) = CreateSystem();

        var executed = new List<AgentToolExecutedEvent>();
        using (eventBus.Subscribe<AgentToolExecutedEvent>((e, _) => { executed.Add(e); return Task.CompletedTask; }))
        {
            var callCount = 0;
            var provider = new MockAIProvider(request =>
            {
                callCount++;
                if (callCount == 1)
                    return AIResponse.WithToolCalls(new[]
                    {
                        new AIToolCall("call-1", "web_search", new Dictionary<string, string>
                        {
                            ["action"] = "search",
                            ["query"] = "recherche des informations sur les technologies"
                        })
                    });
                return AIResponse.Text("Voici les résultats de la recherche.");
            });

            var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
            var aiService = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);

            var result = await aiService.ChatAsync("Cherche des actualités sur l'IA");

            Assert.True(result.Success);
            Assert.Equal("Voici les résultats de la recherche.", result.Content);
            Assert.Equal(2, callCount);
        }

        Assert.Contains(executed,
            e => e.ToolName == "web_search" && e.Success && (e.Result ?? "").Contains("Résultats pour", StringComparison.Ordinal));
    }

    // ─── TEST 2: ToolExecutor search action ─────────────────────────────────

    [Fact]
    public async Task ToolExecutor_executes_web_search_search_action()
    {
        var (registry, eventBus, _) = CreateSystem();
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);

        var result = await executor.ExecuteAsync("web_search",
            Context(Args("action", "search", "query", "recherche des informations sur les technologies", "max_results", "5")));

        Assert.True(result.Success);
        Assert.Contains("Résultats pour", result.Output);
        Assert.Contains("Consensus", result.Output);
        Assert.Contains("Sample News Article", result.Output);
        Assert.Contains("https://sample-news.example.org/2026/08/ai", result.Output);
    }

    // ─── TEST 3: ToolExecutor open action ───────────────────────────────────

    [Fact]
    public async Task ToolExecutor_executes_web_search_open_action()
    {
        var (registry, eventBus, opened) = CreateSystem();
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);

        var result = await executor.ExecuteAsync("web_search",
            Context(Args("action", "open", "query", "https://sample-news.example.org/2026/08/ai")));

        Assert.True(result.Success);
        Assert.Contains("Ouvert dans le navigateur", result.Output);
        Assert.Contains("https://sample-news.example.org/2026/08/ai", opened);
    }

    // ─── TEST 4: Events are published on the bus ────────────────────────────

    [Fact]
    public async Task ToolExecutor_publishes_started_and_executed_events_for_web_search()
    {
        var (registry, eventBus, _) = CreateSystem();

        var started = new List<AgentToolStartedEvent>();
        var executed = new List<AgentToolExecutedEvent>();
        using (eventBus.Subscribe<AgentToolStartedEvent>((e, _) => { started.Add(e); return Task.CompletedTask; }))
        using (eventBus.Subscribe<AgentToolExecutedEvent>((e, _) => { executed.Add(e); return Task.CompletedTask; }))
        {
            var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
            await executor.ExecuteAsync("web_search", Context(Args("action", "search", "query", "test")));
        }

        var startedEvent = Assert.Single(started);
        Assert.Equal("web_search", startedEvent.ToolName);
        Assert.Contains(executed, e => e.ToolName == "web_search" && e.Success);
    }

    // ─── TEST 5: Security gate blocks web_search ────────────────────────────

    [Fact]
    public async Task Security_whitelist_blocks_web_search()
    {
        var (registry, eventBus, _) = CreateSystem();

        var confirmation = new MockConfirmationService();
        var security = new SecurityManager(
            new Lazy<IToolRegistry>(() => registry),
            confirmation,
            eventBus,
            NullLogger<SecurityManager>.Instance,
            new SecurityOptions
            {
                RequireConfirmationForHighRisk = true,
                AllowDisableConfirmation = false,
                WhitelistedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "date_time" }
            });

        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance, security);

        var result = await executor.ExecuteAsync("web_search", Context(Args("action", "search", "query", "test")));

        Assert.False(result.Success);
        Assert.Contains("security", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }
}
