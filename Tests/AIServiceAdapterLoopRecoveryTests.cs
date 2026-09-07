using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class AIServiceAdapterLoopRecoveryTests
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

    private static AIServiceAdapter CreateAdapter(IAIProvider provider, ITaskExecutionHistory? history)
    {
        var registry = CreateRegistry();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var inner = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);
        var router = new ModelRouter(new ModelRouterOptions(), NullLogger<ModelRouter>.Instance);
        var options = new AIOptions { HiddenPlanningEnabled = false, SelfVerificationEnabled = false };
        return new AIServiceAdapter(inner, provider, router, registry, executor, NullLogger<AIServiceAdapter>.Instance, null!,
            taskHistory: history, options: options);
    }

    [Fact]
    public async Task Repeated_tool_call_injects_corrective_message_and_model_recovers()
    {
        var callCount = 0;
        List<AIMessage>? lastMessages = null;
        var provider = new StreamingToolCallProvider(request =>
        {
            callCount++;
            lastMessages = request.Messages.ToList();
            if (callCount <= 3)
            {
                return AIResponse.WithToolCalls(new[]
                {
                    new AIToolCall("call-1", "date_time", new Dictionary<string, string>())
                });
            }
            if (callCount == 4)
            {
                return AIResponse.WithToolCalls(new[]
                {
                    new AIToolCall("call-2", "system_info", new Dictionary<string, string>())
                });
            }
            return AIResponse.Text("Réponse finale : tout est fait.");
        });

        var adapter = CreateAdapter(provider, null);
        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("Quelle heure est-il ?"))
            tokens.Add(token);

        Assert.Equal(5, callCount);
        Assert.Contains("Réponse finale : tout est fait.", string.Join("", tokens));
        Assert.NotNull(lastMessages);
        Assert.Contains(lastMessages!, m => m.Role == AIMessageRole.System && m.Content.Contains("ALERTE ANTI-BOUCLE"));
    }

    [Fact]
    public async Task Repeated_tool_call_aborts_after_recoveries_exhausted()
    {
        var callCount = 0;
        var provider = new StreamingToolCallProvider(_ =>
        {
            callCount++;
            return AIResponse.WithToolCalls(new[]
            {
                new AIToolCall("call-1", "date_time", new Dictionary<string, string>())
            });
        });

        var history = new InMemoryTaskExecutionHistory();
        var adapter = CreateAdapter(provider, history);

        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("Quelle heure est-il ?"))
            tokens.Add(token);

        Assert.Equal(5, callCount);
        Assert.Empty(string.Join("", tokens));
        var record = history.GetRecentHistory().Single();
        Assert.Contains(record.Steps, s => s.StageName == "Error" && s.Description.Contains("Anti-boucle"));
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
}
