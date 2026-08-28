using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class AIServiceAdapterFixesTests
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

    private static AIServiceAdapter CreateAdapter(IAIProvider provider, out AIService inner)
    {
        var registry = CreateRegistry();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        inner = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);
        var router = new ModelRouter(new ModelRouterOptions(), NullLogger<ModelRouter>.Instance);
        return new AIServiceAdapter(inner, provider, router, registry, executor, NullLogger<AIServiceAdapter>.Instance);
    }

    private static AIServiceAdapter CreateAdapter(IAIProvider provider, ITaskExecutionHistory history, out AIService inner)
    {
        var registry = CreateRegistry();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        inner = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);
        var router = new ModelRouter(new ModelRouterOptions(), NullLogger<ModelRouter>.Instance);
        return new AIServiceAdapter(inner, provider, router, registry, executor, NullLogger<AIServiceAdapter>.Instance, history);
    }

    // ─── Model capabilities (no tools for vision / embedding models) ──────

    [Theory]
    [InlineData("llava:7b", false)]
    [InlineData("llava", false)]
    [InlineData("bakllava", false)]
    [InlineData("nomic-embed-text", false)]
    [InlineData("qwen3.5:2b", true)]
    [InlineData("llama3.1", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    public void Model_capabilities_SupportsTools(string? model, bool expected)
    {
        Assert.Equal(expected, ModelCapabilities.SupportsTools(model));
    }

    // ─── Bug 1: vision model must not receive tools / tool loop ───────────

    [Fact]
    public async Task StreamChat_with_llava_model_sends_no_tools_and_answers_directly()
    {
        AIRequest? captured = null;
        var callCount = 0;
        var provider = new MockAIProvider(request =>
        {
            callCount++;
            captured = request;
            return AIResponse.Text("Salut ! Je suis Jarvis, une intelligence artificielle.");
        });

        var adapter = CreateAdapter(provider, out _);
        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("Décris cette image", model: "llava:7b"))
        {
            tokens.Add(token);
        }

        Assert.Equal(1, callCount);
        Assert.NotNull(captured);
        Assert.Empty(captured!.Tools);
        Assert.Equal("llava:7b", captured.Model);
        Assert.Equal("Salut ! Je suis Jarvis, une intelligence artificielle.", string.Join("", tokens));
    }

    // ─── Bug 2: a plain greeting is not a refusal → single response ───────

    [Fact]
    public async Task StreamChat_greeting_is_not_repeated_when_not_a_refusal()
    {
        var callCount = 0;
        var provider = new MockAIProvider(request =>
        {
            callCount++;
            return AIResponse.Text("Salut ! Comment puis-je vous aider aujourd'hui ?");
        });

        var adapter = CreateAdapter(provider, out _);
        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("Hello, how are you today?"))
        {
            tokens.Add(token);
        }

        var fullOutput = string.Join("", tokens);
        Assert.Equal(1, callCount);
        Assert.Contains("Salut ! Comment puis-je vous aider aujourd'hui ?", fullOutput);
    }

    // ─── Bug 2: consecutive refusals are capped (no runaway re-prompting) ─

    [Fact]
    public async Task StreamChat_caps_consecutive_refusal_re_prompts()
    {
        var callCount = 0;
        var provider = new MockAIProvider(request =>
        {
            callCount++;
            return AIResponse.Text("I cannot access your files. You must run the command manually.");
        });

        var adapter = CreateAdapter(provider, out _);
        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("Supprime ce fichier"))
        {
            tokens.Add(token);
        }

        Assert.Equal(2, callCount);
        Assert.Contains("I cannot access your files.", string.Join("", tokens));
    }

    // ─── Task execution history recording ─────────────────────────────────

    [Fact]
    public async Task StreamChat_records_task_history_with_thought_and_final_steps()
    {
        var provider = new MockAIProvider(request => AIResponse.Text("Réponse finale."));
        var history = new InMemoryTaskExecutionHistory();
        var adapter = CreateAdapter(provider, history, out _);

        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("Quelle est la capitale de la France ?")) { tokens.Add(token); }

        var recent = history.GetRecentHistory();
        Assert.Single(recent);
        var record = recent[0];
        Assert.True(record.Success);
        Assert.Equal("Quelle est la capitale de la France ?", record.UserMessage);
        Assert.Equal("Réponse finale.", record.FinalResponse);
        Assert.Contains(record.Steps, s => s.StageName == "Thought");
        Assert.Contains(record.Steps, s => s.StageName == "Final");
        Assert.Equal(0, record.ToolCallCount);
    }

    [Fact]
    public async Task StreamChat_records_tool_steps_when_tools_are_called()
    {
        var registry = CreateRegistry();
        var callCount = 0;
        IAIProvider provider = new StreamingToolCallProvider(request =>
        {
            callCount++;
            return callCount == 1
                ? AIResponse.WithToolCalls(new[]
                {
                    new AIToolCall("call_1", "date_time", new Dictionary<string, string>())
                })
                : AIResponse.Text("Final answer: date tool executed.");
        });

        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var inner = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);
        var router = new ModelRouter(new ModelRouterOptions(), NullLogger<ModelRouter>.Instance);
        var history = new InMemoryTaskExecutionHistory();
        var adapter = new AIServiceAdapter(inner, provider, router, registry, executor, NullLogger<AIServiceAdapter>.Instance, history);

        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("What time is it")) { tokens.Add(token); }

        var record = history.GetRecentHistory().Single();
        Assert.True(record.Success);
        Assert.Equal(1, record.ToolCallCount);
        Assert.Contains(record.Steps, s => s.StageName == "Tool" && s.Description.Contains("date_time") && s.Success);
        Assert.Contains(record.Steps, s => s.StageName == "Final");
    }

    [Fact]
    public async Task StreamChat_records_error_when_llm_fails()
    {
        var provider = new MockAIProvider(request => throw new InvalidOperationException("provider down"));
        var history = new InMemoryTaskExecutionHistory();
        var adapter = CreateAdapter(provider, history, out _);

        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("Test")) { tokens.Add(token); }

        var record = history.GetRecentHistory().Single();
        Assert.False(record.Success);
        Assert.Contains(record.Steps, s => s.StageName == "Error");
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

        public async IAsyncEnumerable<AIStreamChunk> StreamChatAsync(AIRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
}
