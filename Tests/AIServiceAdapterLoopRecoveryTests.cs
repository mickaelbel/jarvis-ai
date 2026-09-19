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
        return CreateAdapter(provider, history, registry);
    }

    private static AIServiceAdapter CreateAdapter(IAIProvider provider, ITaskExecutionHistory? history, ToolRegistry registry)
    {
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
        // Tool status updates are now yielded (✓/✗ indicators), so output is not empty
        // but the final response should be empty or just status markers
        var output = string.Join("", tokens);
        Assert.DoesNotContain("Réponse finale", output);
        var record = history.GetRecentHistory().Single();
        Assert.Contains(record.Steps, s => s.StageName == "Error" && s.Description.Contains("Anti-boucle"));
    }

    [Fact]
    public async Task Max_rounds_without_answer_returns_honest_progress_summary()
    {
        var history = new InMemoryTaskExecutionHistory();
        var provider = new ForcedFinalEmptyProvider();

        var adapter = CreateAdapter(provider, history);

        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("ouvre paint et dessine une fusée"))
            tokens.Add(token);

        var output = string.Join("", tokens);
        Assert.Contains("limite d'itérations", output);
        Assert.Contains("ouvre paint et dessine une fusée", output);
        Assert.DoesNotContain("Peux-tu reformuler ta demande", output);

        var record = history.GetRecentHistory().Single();
        Assert.False(record.Success);
        Assert.Contains(record.Steps.ToList(), s => s.StageName == "Tool");
    }

    /// <summary>Exécute UN appel d'outil réel au round 1, puis renvoie des réponses vides
    /// jusqu'à épuisement des rounds → le modèle ne peut pas donner de réponse finale
    /// exploitable et le repli honnête (bilan des étapes réelles) doit sortir.</summary>
    private sealed class ForcedFinalEmptyProvider : IAIProvider
    {
        private int _requestCount;
        public string Name => "ForcedFinalMock";
        public bool IsAvailable => true;
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsAvailable);
        public IReadOnlyList<string> KnownModels => Array.Empty<string>();
        public bool MatchesModel(string? model) => false;

        public Task<AIResponse> ChatAsync(AIRequest request, CancellationToken cancellationToken = default)
        {
            _requestCount++;
            if (_requestCount == 1 && request.Tools.Count > 0)
                return Task.FromResult(AIResponse.WithToolCalls(new[]
                {
                    new AIToolCall("call-1", "date_time", new Dictionary<string, string>())
                }));
            return Task.FromResult(AIResponse.Text(string.Empty));
        }

        public async IAsyncEnumerable<AIStreamChunk> StreamChatAsync(
            AIRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await ChatAsync(request, cancellationToken);
            if (response.ToolCalls is { Count: > 0 })
            {
                yield return new AIStreamChunk(ToolCalls: response.ToolCalls);
                yield break;
            }
            if (!string.IsNullOrEmpty(response.Content))
                yield return new AIStreamChunk(Token: response.Content);
        }
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

    [Fact]
    public async Task Cancel_during_tool_aborts_stream_without_extra_llm_round()
    {
        // Un outil "bloquant" signalé au démarrage puis suspendu sur son token :
        // il reproduit un computer_action/Ocr long pendant lequel l'utilisateur
        // clique Stop. L'outil rend Failed("Opération annulée.") sans exception,
        // et l'adapter doit alors couper le stream au lieu de relancer un round LLM.
        int llmRounds = 0;
        var blocked = new BlockingTool();
        var registry = CreateRegistry();
        registry.Register(blocked);

        var provider = new StreamingToolCallProvider(_ =>
        {
            llmRounds++;
            return AIResponse.WithToolCalls(new[]
            {
                new AIToolCall("call-1", "blocking_tool", new Dictionary<string, string>())
            });
        });

        var adapter = CreateAdapter(provider, null, registry);
        using var outer = new CancellationTokenSource();
        var run = Task.Run(async () =>
        {
            await foreach (var _ in adapter.StreamChatAsync("bloque-moi", cancellationToken: outer.Token))
            { }
        });

        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        outer.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, llmRounds);
        Assert.Equal(1, blocked.Executions);
    }

    [Fact]
    public async Task Preamble_text_from_tool_round_is_not_streamed_as_answer()
    {
        // Le modèle diffuse d'abord de la prose ("Je vais ouvrir…") PUIS, dans le
        // MÊME round, un appel d'outil. Cette prose ne doit PAS être diffusée comme
        // une réponse : sinon l'utilisateur croit que l'agent a déjà répondu alors
        // que l'outil (ex: computer_action qui tape du texte) va s'exécuter après.
        var provider = new PreambleThenToolProvider(
            preamble: "Je vais ouvrir Blender et taper blender.",
            final: "Blender n'a pas été trouvé sur le système.");

        var adapter = CreateAdapter(provider, null);
        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("ouvre blender et supprime le cube"))
            tokens.Add(token);

        var output = string.Join("", tokens);
        Assert.DoesNotContain("Je vais ouvrir Blender", output);
        Assert.Contains("Blender n'a pas été trouvé sur le système.", output);
    }

    private sealed class PreambleThenToolProvider : IAIProvider
    {
        private readonly string _preamble;
        private readonly string _final;
        private int _requestCount;
        public string Name => "PreambleMock";
        public bool IsAvailable => true;
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsAvailable);
        public IReadOnlyList<string> KnownModels => Array.Empty<string>();
        public bool MatchesModel(string? model) => false;

        public PreambleThenToolProvider(string preamble, string final)
        {
            _preamble = preamble;
            _final = final;
        }

        public Task<AIResponse> ChatAsync(AIRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(AIResponse.Text(_final));

        public async IAsyncEnumerable<AIStreamChunk> StreamChatAsync(
            AIRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            _requestCount++;
            if (_requestCount == 1)
            {
                // Prose de préambule PUIS appel d'outil dans le même round.
                yield return new AIStreamChunk(Token: _preamble);
                yield return new AIStreamChunk(ToolCalls: new[]
                {
                    new AIToolCall("call-1", "date_time", new Dictionary<string, string>())
                });
                yield break;
            }
            yield return new AIStreamChunk(Token: _final);
        }
    }

    private sealed class BlockingTool : ToolBase
    {
        private int _executions;
        public int Executions => _executions;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingTool() : base(NullLogger<BlockingTool>.Instance) { }
        public override string Name => "blocking_tool";
        public override string Description => "Outil bloquant pour test";
        public override string Category => "test";

        protected override async Task<ToolResult> ExecuteCoreAsync(AgentContext context,
            IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            _executions++;
            Started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return ToolResult.Succeeded("inatteignable");
        }
    }
}
