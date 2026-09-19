using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class AuditPhase1RegressionTests
{
    private static IMemoryService CreateMemoryService()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        return new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
    }

    private static ToolRegistry CreateRegistry()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(new DateTimeTool());
        registry.Register(new SystemInfoTool());
        return registry;
    }

    private static AIServiceAdapter CreateAdapter(IAIProvider provider, AIOptions? options = null)
    {
        var registry = CreateRegistry();
        return CreateAdapter(provider, options, registry);
    }

    private static AIServiceAdapter CreateAdapter(IAIProvider provider, AIOptions? options, ToolRegistry registry)
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var inner = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);
        var router = new ModelRouter(new ModelRouterOptions(), NullLogger<ModelRouter>.Instance);
        options ??= new AIOptions { HiddenPlanningEnabled = false, SelfVerificationEnabled = false };
        return new AIServiceAdapter(inner, provider, router, registry, executor, NullLogger<AIServiceAdapter>.Instance, null!,
            options: options);
    }

    private sealed class StreamingToolCallProvider : IAIProvider
    {
        private readonly Func<AIRequest, AIResponse> _responder;
        public string Name => "StreamingMock";
        public bool IsAvailable => true;
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsAvailable);
        public IReadOnlyList<string> KnownModels => Array.Empty<string>();
        public bool MatchesModel(string? model) => false;

        public StreamingToolCallProvider(Func<AIRequest, AIResponse> responder) => _responder = responder;

        public Task<AIResponse> ChatAsync(AIRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_responder(request));

        public async IAsyncEnumerable<AIStreamChunk> StreamChatAsync(
            AIRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            var response = _responder(request);
            if (response.ToolCalls is { Count: > 0 })
            {
                yield return new AIStreamChunk(ToolCalls: response.ToolCalls);
                yield break;
            }
            if (!string.IsNullOrEmpty(response.Content))
                yield return new AIStreamChunk(Token: response.Content);
        }
    }

    // ── BUG-003: Tool status updates are streamed ──────────────────────────

    [Fact]
    public async Task Tool_status_updates_are_yielded_during_execution()
    {
        var callCount = 0;
        var provider = new StreamingToolCallProvider(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                return AIResponse.WithToolCalls(new[]
                {
                    new AIToolCall("call-1", "date_time", new Dictionary<string, string>())
                });
            }
            return AIResponse.Text("Voici la date.");
        });

        var adapter = CreateAdapter(provider);
        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("Quelle heure est-il ?"))
            tokens.Add(token);

        var output = string.Join("", tokens);
        Assert.Contains("date_time", output);
        Assert.Contains("Voici la date.", output);
    }

    // ── BUG-002: Self-verification triggers for multi-tool tasks ───────────

    [Fact]
    public async Task Multi_tool_task_completes_successfully()
    {
        var callCount = 0;
        var provider = new StreamingToolCallProvider(request =>
        {
            callCount++;
            if (callCount <= 2)
            {
                var toolName = callCount == 1 ? "date_time" : "system_info";
                return AIResponse.WithToolCalls(new[]
                {
                    new AIToolCall($"call-{callCount}", toolName, new Dictionary<string, string>())
                });
            }
            return AIResponse.Text("Voici les informations demandées.");
        });

        var options = new AIOptions
        {
            HiddenPlanningEnabled = false,
            SelfVerificationEnabled = true
        };
        var adapter = CreateAdapter(provider, options);

        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("Donne-moi la date et les infos système"))
            tokens.Add(token);

        var output = string.Join("", tokens);
        Assert.Contains("Voici les informations demandées.", output);
    }

    // ── BUG-005: System prompt contains enhanced rules ─────────────────────

    [Fact]
    public void System_prompt_contains_enhanced_tool_rules()
    {
        var prompt = AgentSystemPrompt.Build(new[]
        {
            new AIToolDefinition("computer_action", "Test", new Dictionary<string, AIToolProperty>()),
            new AIToolDefinition("browser", "Test", new Dictionary<string, AIToolProperty>())
        });

        Assert.Contains("RÈGLES OUTILS", prompt);
        Assert.Contains("computer_action", prompt);
        Assert.Contains("browser", prompt);
        Assert.Contains("ACTION TERMINÉE", prompt);
        Assert.Contains("Jamais browser pour du local", prompt);
    }

    // ── BUG-008: Context-aware anti-loop recovery ──────────────────────────

    [Fact]
    public async Task Anti_loop_recovery_is_context_aware()
    {
        var callCount = 0;
        var lastSystemMessage = "";
        var provider = new StreamingToolCallProvider(request =>
        {
            callCount++;
            var sysMsgs = request.Messages.Where(m =>
                m.Role == AIMessageRole.System &&
                m.Content.Contains("ANTI-BOUCLE")).ToList();
            if (sysMsgs.Count > 0)
                lastSystemMessage = sysMsgs.Last().Content;

            return AIResponse.WithToolCalls(new[]
            {
                new AIToolCall("call-1", "computer_action",
                    new Dictionary<string, string> { ["instruction"] = "test" })
            });
        });

        var adapter = CreateAdapter(provider);
        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("Fais quelque chose"))
            tokens.Add(token);

        Assert.Contains("ANTI-BOUCLE", lastSystemMessage);
        Assert.Contains("OBSERVE", lastSystemMessage);
    }

    // ── BUG-010: Condensation preserves tool context ───────────────────────

    [Fact]
    public void Condenser_constructor_requires_provider_and_logger()
    {
        var provider = new StreamingToolCallProvider(_ => AIResponse.Text("ok"));
        var condenser = new ConversationCondenser(provider, NullLogger<ConversationCondenser>.Instance);
        Assert.NotNull(condenser);
    }

    // ── BUG-006: Auto routing uses classifier ──────────────────────────────

    [Fact]
    public void Model_router_classifies_simple_queries_as_fast()
    {
        var router = new ModelRouter(
            new ModelRouterOptions(FastModel: "llama3.1:latest", ReasoningModel: "qwen3:8b"),
            NullLogger<ModelRouter>.Instance);

        var result = router.Resolve("salut", null, ModelSelectionMode.Auto);
        Assert.Equal("llama3.1:latest", result.Model);
        Assert.Equal(ModelProfile.Fast, result.Profile);
    }

    [Fact]
    public void Model_router_classifies_complex_queries_as_reasoning()
    {
        var router = new ModelRouter(
            new ModelRouterOptions(FastModel: "llama3.1:latest", ReasoningModel: "qwen3:8b"),
            NullLogger<ModelRouter>.Instance);

        var result = router.Resolve("explique-moi le pattern observer en détail avec un exemple de code", null, ModelSelectionMode.Auto);
        Assert.Equal("qwen3:8b", result.Model);
        Assert.Equal(ModelProfile.Reasoning, result.Profile);
    }

    // ── ToolResult supports images ─────────────────────────────────────────

    [Fact]
    public void ToolResult_can_carry_images()
    {
        var images = new[] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } };
        var result = ToolResult.Succeeded("test").WithImages(images);

        Assert.True(result.Success);
        Assert.NotNull(result.Images);
        Assert.Equal(2, result.Images!.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Images[0]);
    }

    [Fact]
    public void ToolResult_WithMeta_preserves_images()
    {
        var images = new[] { new byte[] { 1, 2, 3 } };
        var result = ToolResult.Succeeded("test").WithImages(images).WithMeta("tool", 100);

        Assert.True(result.Success);
        Assert.NotNull(result.Images);
        Assert.Single(result.Images!);
        Assert.Equal("tool", result.ToolName);
        Assert.Equal(100, result.DurationMs);
    }
}
