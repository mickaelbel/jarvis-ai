using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Context;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Plugins;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class ContextBuilderTests
{
    private static ContextBuilder Create(
        IPluginManager? plugins = null,
        IMemoryService? memory = null,
        ToolRegistry? registry = null,
        IAIProvider? provider = null)
    {
        var eventBus = new JarvisAI.Infrastructure.Events.InMemoryEventBus(NullLogger<JarvisAI.Infrastructure.Events.InMemoryEventBus>.Instance);
        return new ContextBuilder(
            memory ?? new FakeMemoryService(),
            plugins ?? new FakePluginManager(),
            registry ?? new ToolRegistry(NullLogger<ToolRegistry>.Instance),
            new FakeSelfImprovementManager(),
            provider ?? new MockAIProvider(AIResponse.Text("ok")),
            new ModelRouter(new ModelRouterOptions { FastModel = "llama3", ReasoningModel = "llama3:70b" }, NullLogger<ModelRouter>.Instance),
            NullLogger<ContextBuilder>.Instance);
    }

    private static AgentRequest Request(string goal = "test goal") => new() { Goal = goal, Source = "test" };

    [Fact]
    public async Task BuildAsync_returns_bundle_with_goal_and_correlation_id()
    {
        var builder = Create();
        var bundle = await builder.BuildAsync(Request("mon objectif"));

        Assert.Equal("mon objectif", bundle.Goal);
        Assert.NotEqual(Guid.Empty, bundle.CorrelationId);
        Assert.Equal("test", bundle.Source);
    }

    [Fact]
    public async Task BuildAsync_uses_request_correlation_id()
    {
        var builder = Create();
        var id = Guid.NewGuid();
        var bundle = await builder.BuildAsync(new AgentRequest { Goal = "g", CorrelationId = id });
        Assert.Equal(id, bundle.CorrelationId);
    }

    [Fact]
    public async Task BuildAsync_returns_expected_sections()
    {
        var builder = Create();
        var bundle = await builder.BuildAsync(Request());
        var titles = bundle.Sections.Select(s => s.Title).ToList();

        Assert.Contains("SYSTEM", titles);
        Assert.Contains("OLLAMA", titles);
        Assert.Contains("ACTIVE PLUGINS", titles);
        Assert.Contains("AVAILABLE TOOLS", titles);
        Assert.Contains("MEMORY", titles);
        Assert.Contains("CONVERSATION", titles);
    }

    [Fact]
    public async Task BuildAsync_collects_active_plugins()
    {
        var plugins = new FakePluginManager(metadata: new PluginMetadata { Id = "p1", Name = "System", State = PluginState.Running });
        var builder = Create(plugins: plugins);
        var bundle = await builder.BuildAsync(Request());
        Assert.Contains(bundle.ActivePlugins, p => p.Contains("System"));
    }

    [Fact]
    public async Task BuildAsync_ignores_inactive_plugins()
    {
        var plugins = new FakePluginManager(metadata: new PluginMetadata { Id = "p1", Name = "System", State = PluginState.Discovered });
        var builder = Create(plugins: plugins);
        var bundle = await builder.BuildAsync(Request());
        Assert.DoesNotContain(bundle.ActivePlugins, p => p.Contains("System"));
    }

    [Fact]
    public async Task BuildAsync_tolerates_plugin_errors()
    {
        var builder = Create(plugins: new FakePluginManager(throwOnEnumerate: true));
        var bundle = await builder.BuildAsync(Request());
        Assert.Empty(bundle.ActivePlugins);
    }

    [Fact]
    public async Task BuildAsync_lists_available_tools_from_registry()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(new TestTool("date_time", "time"));
        var builder = Create(registry: registry);
        var bundle = await builder.BuildAsync(Request());
        Assert.Contains(bundle.AvailableTools, t => t.Name == "date_time");
        Assert.Contains(bundle.ToolNames, n => n == "date_time");
    }

    [Fact]
    public async Task BuildAsync_reports_no_memories_when_empty()
    {
        var builder = Create(memory: new FakeMemoryService(buildContext: _ => Task.FromResult(new MemoryContext())));
        var bundle = await builder.BuildAsync(Request());
        var memorySection = bundle.Sections.First(s => s.Title == "MEMORY");
        Assert.Contains("No relevant memories", memorySection.Content);
    }

    [Fact]
    public async Task BuildAsync_includes_memories_when_present()
    {
        var memoryContext = new MemoryContext { LongTermMemories = new List<MemoryEntry> { new() { Content = "le serveur tourne sous linux" } } };
        var builder = Create(memory: new FakeMemoryService(buildContext: _ => Task.FromResult(memoryContext)));
        var bundle = await builder.BuildAsync(Request());
        var memorySection = bundle.Sections.First(s => s.Title == "MEMORY");
        Assert.Contains("le serveur tourne sous linux", memorySection.Content);
    }

    [Fact]
    public async Task BuildAsync_tolerates_memory_failures()
    {
        var builder = Create(memory: new FakeMemoryService(buildContext: _ => throw new InvalidOperationException("store down")));
        var bundle = await builder.BuildAsync(Request());
        var memorySection = bundle.Sections.First(s => s.Title == "MEMORY");
        Assert.Contains("Memory unavailable", memorySection.Content);
    }

    [Fact]
    public async Task BuildAsync_includes_recent_conversation_from_metadata()
    {
        var builder = Create();
        var bundle = await builder.BuildAsync(new AgentRequest
        {
            Goal = "g",
            Metadata = new Dictionary<string, string> { ["recent_conversation"] = "user: bonjour" }
        });
        var conversation = bundle.Sections.First(s => s.Title == "CONVERSATION");
        Assert.Contains("user: bonjour", conversation.Content);
    }

    [Fact]
    public async Task BuildAsync_reports_no_conversation_when_absent()
    {
        var builder = Create();
        var bundle = await builder.BuildAsync(Request());
        var conversation = bundle.Sections.First(s => s.Title == "CONVERSATION");
        Assert.Contains("No recent conversation", conversation.Content);
    }

    [Fact]
    public async Task BuildAsync_reports_ollama_status()
    {
        var builder = Create(provider: new MockAIProvider(AIResponse.Text("ok")));
        var bundle = await builder.BuildAsync(Request());
        var ollama = bundle.Sections.First(s => s.Title == "OLLAMA");
        Assert.Contains("Ollama is available", ollama.Content);
        Assert.Equal(ollama.Content, bundle.OllamaStatus);
    }

    [Fact]
    public void Render_caps_total_length()
    {
        var bundle = new ContextBundle(
            goal: "g",
            correlationId: Guid.NewGuid(),
            mode: ModelSelectionMode.Auto,
            relevantMemories: Array.Empty<MemoryEntry>(),
            activePlugins: Array.Empty<string>(),
            availableTools: Array.Empty<ITool>(),
            ollamaStatus: null,
            sections: new[]
            {
                new ContextSection("SYSTEM", new string('a', 3000), 4000),
                new ContextSection("MEMORY", new string('b', 3000), 4000)
            });

        var rendered = bundle.Render(500);
        Assert.True(rendered.Length <= 500);
        Assert.EndsWith("...", rendered);
    }

    [Fact]
    public void ContextSection_truncates_long_content()
    {
        var section = new ContextSection("TEST", new string('x', 5000), 256);
        Assert.True(section.Content.Length <= 256);
        Assert.EndsWith("...", section.Content);
    }

    [Fact]
    public void ContextSection_empty_content_renders_none()
    {
        var section = new ContextSection("TEST", "   ");
        Assert.Equal("(none)", section.Content);
        Assert.Equal("=== TEST ===\n(none)", section.Render());
    }
}
