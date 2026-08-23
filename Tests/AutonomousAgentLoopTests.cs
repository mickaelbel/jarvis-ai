using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class AutonomousAgentLoopTests
{
    private static IMemoryService CreateMemoryService()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        return new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
    }

    private static (AutonomousAgentLoop Loop, EchoTool Echo) CreateLoop(
        MockAIProvider provider,
        AutonomousLoopOptions? options = null,
        IObservationProvider? observer = null)
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var echo = new EchoTool();
        registry.Register(echo);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var aiService = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);
        var loop = new AutonomousAgentLoop(
            aiService,
            provider,
            observer ?? new FakeObserver(),
            CreateMemoryService(),
            options ?? new AutonomousLoopOptions { MaxIterations = 5, MaxConsecutiveErrors = 2 },
            NullLogger<AutonomousAgentLoop>.Instance);
        return (loop, echo);
    }

    private static bool IsVerificationRequest(AIRequest request)
        => request.SystemPrompt.Contains("verification agent");

    // ─── SUCCESS ON FIRST ITERATION ───────────────────────────────────────

    [Fact]
    public async Task Returns_success_when_goal_verified_on_first_iteration()
    {
        var provider = new MockAIProvider(request =>
        {
            if (IsVerificationRequest(request))
                return AIResponse.Text("{\"verified\": true, \"reason\": \"done\", \"correction\": \"\"}");
            return AIResponse.Text("Task completed successfully.");
        });

        var (loop, _) = CreateLoop(provider);
        var result = await loop.ExecuteAsync("Open the file");

        Assert.True(result.Success);
        Assert.Equal("Task completed successfully.", result.FinalResponse);
        Assert.Equal(1, result.Iterations);
        Assert.Equal(0, result.Corrections);
    }

    // ─── CORRECTION THEN SUCCESS ──────────────────────────────────────────

    [Fact]
    public async Task Applies_correction_and_succeeds_on_next_iteration()
    {
        var verificationCalls = 0;
        var provider = new MockAIProvider(request =>
        {
            if (IsVerificationRequest(request))
            {
                verificationCalls++;
                if (verificationCalls == 1)
                    return AIResponse.Text("```json\n{\"verified\": false, \"reason\": \"not done\", \"correction\": \"click the submit button\"}\n```");
                return AIResponse.Text("{\"verified\": true, \"reason\": \"done now\", \"correction\": \"\"}");
            }
            return AIResponse.Text("Action output.");
        });

        var (loop, _) = CreateLoop(provider);
        var result = await loop.ExecuteAsync("Fill the form");

        Assert.True(result.Success);
        Assert.Equal(2, result.Iterations);
        Assert.Equal(1, result.Corrections);
    }

    // ─── NEVER VERIFIED → MAX ITERATIONS ──────────────────────────────────

    [Fact]
    public async Task Fails_after_max_iterations_when_never_verified()
    {
        var provider = new MockAIProvider(request =>
        {
            if (IsVerificationRequest(request))
                return AIResponse.Text("{\"verified\": false, \"reason\": \"still not done\", \"correction\": \"keep trying\"}");
            return AIResponse.Text("Action output.");
        });

        var (loop, _) = CreateLoop(provider, new AutonomousLoopOptions { MaxIterations = 3, MaxConsecutiveErrors = 2 });
        var result = await loop.ExecuteAsync("Long task");

        Assert.False(result.Success);
        Assert.Equal(3, result.Iterations);
        Assert.Equal(3, result.Corrections);
        Assert.Contains("Maximum iterations", result.Reason);
    }

    // ─── UNPARSEABLE VERIFICATION → STOPS WITH CORRECTION AVAILABLE ───────

    [Fact]
    public async Task Stops_when_verification_is_unparseable_and_no_correction()
    {
        var provider = new MockAIProvider(request =>
        {
            if (IsVerificationRequest(request))
                return AIResponse.Text("I have no idea what to answer here");
            return AIResponse.Text("Action output.");
        });

        var (loop, _) = CreateLoop(provider);
        var result = await loop.ExecuteAsync("Ambiguous task");

        Assert.False(result.Success);
        Assert.Equal(1, result.Iterations);
        Assert.Contains("no correction", result.Reason);
    }

    // ─── TOOL LOOP INSIDE ACTION ROUND ────────────────────────────────────

    [Fact]
    public async Task Executes_tools_during_action_round_before_verification()
    {
        var actionCalls = 0;
        var provider = new MockAIProvider(request =>
        {
            if (IsVerificationRequest(request))
                return AIResponse.Text("{\"verified\": true, \"reason\": \"done\", \"correction\": \"\"}");
            actionCalls++;
            if (actionCalls == 1)
                return AIResponse.WithToolCalls(new[]
                {
                    new AIToolCall("call-1", "echo", new Dictionary<string, string>())
                });
            return AIResponse.Text("done after tool");
        });

        var (loop, echo) = CreateLoop(provider);
        var result = await loop.ExecuteAsync("Run the echo tool");

        Assert.True(result.Success);
        Assert.Equal(1, echo.CallCount);
        Assert.Equal(1, result.Iterations);
    }

    // ─── CONSECUTIVE ACTION ERRORS → FAILURE ──────────────────────────────

    [Fact]
    public async Task Fails_after_max_consecutive_action_errors()
    {
        var provider = new MockAIProvider(request =>
        {
            if (IsVerificationRequest(request))
                return AIResponse.Text("{\"verified\": false, \"reason\": \"nope\", \"correction\": \"retry\"}");
            return AIResponse.Failed("boom");
        });

        var (loop, _) = CreateLoop(provider, new AutonomousLoopOptions { MaxIterations = 10, MaxConsecutiveErrors = 2 });
        var result = await loop.ExecuteAsync("Do the thing");

        Assert.False(result.Success);
        Assert.Contains("Repeated action errors", result.Reason);
    }

    // ─── EMPTY GOAL ───────────────────────────────────────────────────────

    [Fact]
    public async Task Rejects_empty_goal()
    {
        var provider = new MockAIProvider(_ => AIResponse.Text("should not be called"));
        var (loop, _) = CreateLoop(provider);

        var result = await loop.ExecuteAsync("   ");

        Assert.False(result.Success);
        Assert.Contains("Empty goal", result.Reason);
    }

    private sealed class FakeObserver : IObservationProvider
    {
        public string Text { get; set; } = "Current screen: empty";

        public Task<string> ObserveAsync(string goal, CancellationToken cancellationToken = default)
            => Task.FromResult(Text);
    }

    private sealed class EchoTool : ITool
    {
        public string Name => "echo";
        public string Description => "Echo test tool";
        public string Category => "test";
        public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public int CallCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(ToolResult.Succeeded("echo executed"));
        }
    }
}
