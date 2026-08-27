using System.Runtime.CompilerServices;
using JarvisAI.Application.AI;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class AIServiceTests
{
    private static IMemoryService CreateMemoryService()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        return new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
    }

    private static (AIService aiService, ToolRegistry registry, ToolExecutor executor, InMemoryEventBus eventBus) CreateSystem(IAIProvider? provider = null, IMemoryService? memoryService = null)
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var registryLogger = NullLogger<ToolRegistry>.Instance;
        var executorLogger = NullLogger<ToolExecutor>.Instance;
        var serviceLogger = NullLogger<AIService>.Instance;

        var registry = new ToolRegistry(registryLogger);
        var executor = new ToolExecutor(registry, eventBus, executorLogger);
        provider ??= new MockAIProvider();
        memoryService ??= CreateMemoryService();

        var aiService = new AIService(provider, registry, executor, eventBus, memoryService, serviceLogger);
        return (aiService, registry, executor, eventBus);
    }

    [Fact]
    public async Task AIService_returns_text_when_LLM_responds_directly()
    {
        var provider = new MockAIProvider(AIResponse.Text("Hello! I'm Jarvis."));
        var (aiService, _, _, _) = CreateSystem(provider);

        var result = await aiService.ChatAsync("Hello");

        Assert.True(result.Success);
        Assert.Equal("Hello! I'm Jarvis.", result.Content);
        Assert.False(result.HasToolCalls);
    }

    [Fact]
    public async Task AIService_executes_tool_and_returns_final_response()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(new DateTimeTool());

        var callCount = 0;
        var provider = new MockAIProvider(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                return AIResponse.WithToolCalls(new[]
                {
                    new AIToolCall("call-1", "date_time", new Dictionary<string, string>())
                });
            }
            return AIResponse.Text("It is currently 2025-01-15 10:30:00 UTC.");
        });

        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var aiService = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);

        var result = await aiService.ChatAsync("What time is it?");

        Assert.True(result.Success);
        Assert.Equal("It is currently 2025-01-15 10:30:00 UTC.", result.Content);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task AIService_handles_provider_error()
    {
        var provider = new MockAIProvider(AIResponse.Failed("Connection refused"));
        var (aiService, _, _, _) = CreateSystem(provider);

        var result = await aiService.ChatAsync("Hello");

        Assert.False(result.Success);
        Assert.Contains("Connection refused", result.ErrorMessage);
    }

    [Fact]
    public async Task AIService_handles_tool_execution_error()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(new FailingAITool());

        var callCount = 0;
        var provider = new MockAIProvider(request =>
        {
            callCount++;
            if (callCount == 1)
                return AIResponse.WithToolCalls(new[]
                {
                    new AIToolCall("call-1", "fail_tool", new Dictionary<string, string>())
                });
            return AIResponse.Text("The tool failed but I'll handle it gracefully.");
        });

        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var aiService = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);

        var result = await aiService.ChatAsync("Do something that fails");

        Assert.True(result.Success);
        Assert.Equal("The tool failed but I'll handle it gracefully.", result.Content);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task AIService_produces_tool_definitions_from_registry()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(new DateTimeTool());
        registry.Register(new SystemInfoTool());

        AIRequest? capturedRequest = null;
        var provider = new MockAIProvider(request =>
        {
            capturedRequest = request;
            return AIResponse.Text("ok");
        });

        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var aiService = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);

        await aiService.ChatAsync("test");

        Assert.NotNull(capturedRequest);
        Assert.Equal(2, capturedRequest!.Tools.Count);

        var toolNames = capturedRequest.Tools.Select(t => t.Name).ToList();
        Assert.Contains("date_time", toolNames);
        Assert.Contains("system_info", toolNames);
    }

    [Fact]
    public void AIConversation_maintains_history()
    {
        var conversation = new AIConversation("You are Jarvis.");
        conversation.AddUserMessage("Hello");
        conversation.AddAssistantMessage("Hi there!");
        conversation.AddUserMessage("What time is it?");

        var messages = conversation.ToRequestMessages();
        Assert.Equal(3, messages.Count);
        Assert.Equal(AIMessageRole.User, messages[0].Role);
        Assert.Equal(AIMessageRole.Assistant, messages[1].Role);
        Assert.Equal(AIMessageRole.User, messages[2].Role);
    }

    [Fact]
    public void AIConversation_trim_removes_old_messages()
    {
        var conversation = new AIConversation("System");
        for (int i = 0; i < 10; i++)
            conversation.AddUserMessage($"Message {i}");

        conversation.Trim(3);
        Assert.Equal(3, conversation.Messages.Count);
        Assert.Contains("Message 7", conversation.Messages[0].Content);
    }

    [Fact]
    public void AIConversation_clone_is_independent_snapshot()
    {
        var conversation = new AIConversation("System");
        conversation.AddUserMessage("Hello");
        conversation.AddUserMessage("World");

        var clone = conversation.Clone();
        Assert.Equal(conversation.SystemPrompt, clone.SystemPrompt);
        Assert.Equal(2, clone.Messages.Count);
        Assert.Equal(conversation.Messages[0].Content, clone.Messages[0].Content);

        // Une mutation du clone ne doit PAS affecter l'original (barge-in isolé).
        clone.AddUserMessage("Barge-in");
        Assert.Equal(2, conversation.Messages.Count);
        Assert.Equal(3, clone.Messages.Count);
    }

    [Fact]
    public async Task AIService_stops_after_max_tool_rounds()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(new DateTimeTool());

        var callCount = 0;
        var provider = new MockAIProvider(request =>
        {
            callCount++;
            return AIResponse.WithToolCalls(new[]
            {
                new AIToolCall($"call-{callCount}", "date_time", new Dictionary<string, string>())
            });
        });

        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var aiService = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);

        var result = await aiService.ChatAsync("keep calling tools");

        Assert.False(result.Success);
        Assert.Contains("Maximum tool execution rounds", result.ErrorMessage);
        Assert.Equal(5, callCount);
    }
}

internal sealed class MockAIProvider : IAIProvider
{
    private readonly Func<AIRequest, AIResponse> _responder;
    public string Name => "Mock";
        public bool IsAvailable => true;
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsAvailable);
    public IReadOnlyList<string> KnownModels => Array.Empty<string>();
    public bool MatchesModel(string? model) => false;

    public MockAIProvider() : this(_ => AIResponse.Text("mocked response")) { }

    public MockAIProvider(Func<AIRequest, AIResponse> responder)
    {
        _responder = responder;
    }

    public MockAIProvider(AIResponse fixedResponse) : this(_ => fixedResponse) { }

    public Task<AIResponse> ChatAsync(AIRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(_responder(request));

    public async IAsyncEnumerable<AIStreamChunk> StreamChatAsync(AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        var response = _responder(request);
        if (!string.IsNullOrEmpty(response.Content))
        {
            yield return new AIStreamChunk(Token: response.Content);
        }
        yield break;
    }
}

internal sealed class FailingAITool : ITool
{
    public string Name => "fail_tool";
    public string Description => "Always fails";
    public string Category => "test";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Simulated tool failure");
}
