using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Context;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Planning;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class ReasoningLoopTests
{
    private static ContextBundle CreateContext()
        => new(
            goal: "goal",
            correlationId: Guid.NewGuid(),
            mode: ModelSelectionMode.Auto,
            relevantMemories: Array.Empty<MemoryEntry>(),
            activePlugins: Array.Empty<string>(),
            availableTools: Array.Empty<ITool>(),
            ollamaStatus: "Ollama is available.",
            sections: Array.Empty<ContextSection>());

    private static (ReasoningLoop loop, ToolRegistry registry, IMemoryService memory) CreateLoop(
        Func<AIRequest, AIResponse> responder,
        ReasoningLoopOptions? options = null,
        AutomaticMemoryOptions? memoryOptions = null)
    {
        var provider = new MockAIProvider(responder);
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var store = new InMemoryMemoryStore();
        var memory = new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
        var autoMemory = new AutomaticMemoryService(memory, memoryOptions, NullLogger<AutomaticMemoryService>.Instance);
        var selection = new ToolSelectionService();
        var parallel = new ParallelToolExecutor(executor, registry, new RetryPolicy(new RetryPolicyOptions { BaseDelay = TimeSpan.FromMilliseconds(1) }), NullLogger<ParallelToolExecutor>.Instance);

        var loop = new ReasoningLoop(
            provider,
            registry,
            selection,
            parallel,
            new RetryPolicy(new RetryPolicyOptions { BaseDelay = TimeSpan.FromMilliseconds(1) }),
            autoMemory,
            options ?? new ReasoningLoopOptions(),
            NullLogger<ReasoningLoop>.Instance);

        return (loop, registry, memory);
    }

    private static AIToolCall Call(string name, params (string Key, string Value)[] args)
        => new(Guid.NewGuid().ToString("N")[..12], name, args.ToDictionary(a => a.Key, a => a.Value));

    [Fact]
    public async Task ExecuteAsync_empty_goal_fails()
    {
        var (loop, _, _) = CreateLoop(_ => AIResponse.Text("x"));
        var result = await loop.ExecuteAsync("   ", CreateContext(), null, "model", new AgentContext("goal"), false);
        Assert.False(result.Success);
        Assert.Equal("Empty goal", result.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_returns_final_answer()
    {
        var (loop, _, _) = CreateLoop(_ => AIResponse.Text("Voici la réponse finale."));
        var result = await loop.ExecuteAsync("goal", CreateContext(), null, "model", new AgentContext("goal"), false);
        Assert.True(result.Success);
        Assert.Equal("Voici la réponse finale.", result.FinalResponse);
        Assert.Equal(1, result.Iterations);
    }

    [Fact]
    public async Task ExecuteAsync_executes_tool_then_returns_final_answer()
    {
        var callCount = 0;
        var (loop, registry, _) = CreateLoop(_ =>
        {
            callCount++;
            return callCount == 1
                ? AIResponse.WithToolCalls(new[] { Call("echo", ("text", "hello")) })
                : AIResponse.Text("finished");
        });
        registry.Register(new TestTool("echo", "echoes", handler: (p, _) => Task.FromResult(ToolResult.Succeeded(p["text"]))));

        var result = await loop.ExecuteAsync("goal", CreateContext(), null, "model", new AgentContext("goal"), false);

        Assert.True(result.Success);
        Assert.Equal("finished", result.FinalResponse);
        Assert.Equal(2, result.Iterations);
        Assert.Equal(2, result.Turns.Count);
    }

    [Fact]
    public async Task ExecuteAsync_reaches_max_iterations()
    {
        var (loop, _, _) = CreateLoop(_ => AIResponse.Text(string.Empty), options: new ReasoningLoopOptions { MaxIterations = 4 });
        var result = await loop.ExecuteAsync("goal", CreateContext(), null, "model", new AgentContext("goal"), false);
        Assert.False(result.Success);
        Assert.Contains("Maximum iterations", result.Reason);
        Assert.Equal(4, result.Iterations);
    }

    [Fact]
    public async Task ExecuteAsync_detects_repeated_tool_calls()
    {
        var (loop, registry, _) = CreateLoop(_ => AIResponse.WithToolCalls(new[] { Call("echo", ("text", "same")) }));
        registry.Register(new TestTool("echo", "echoes"));

        var result = await loop.ExecuteAsync("goal", CreateContext(), null, "model", new AgentContext("goal"), false);

        Assert.False(result.Success);
        Assert.Contains("Loop detected", result.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_fails_after_consecutive_llm_errors()
    {
        var (loop, _, _) = CreateLoop(_ => AIResponse.Failed("boom"), options: new ReasoningLoopOptions { MaxConsecutiveErrors = 3 });
        var result = await loop.ExecuteAsync("goal", CreateContext(), null, "model", new AgentContext("goal"), false);
        Assert.False(result.Success);
        Assert.Contains("Repeated LLM errors", result.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_recovers_after_transient_error()
    {
        var callCount = 0;
        var (loop, _, _) = CreateLoop(_ =>
        {
            callCount++;
            return callCount == 1 ? AIResponse.Failed("transient") : AIResponse.Text("recovered!");
        });
        var result = await loop.ExecuteAsync("goal", CreateContext(), null, "model", new AgentContext("goal"), false);
        Assert.True(result.Success);
        Assert.Equal("recovered!", result.FinalResponse);
    }

    [Fact]
    public async Task ExecuteAsync_invokes_onStep_for_thought_and_final()
    {
        var steps = new List<(RunStepKind Kind, string Content)>();
        var (loop, _, _) = CreateLoop(_ => AIResponse.Text("ma réponse"));
        await loop.ExecuteAsync("goal", CreateContext(), null, "model", new AgentContext("goal"), false,
            onStep: (kind, content, _, _, _) => steps.Add((kind, content)));

        Assert.Contains(steps, s => s.Kind == RunStepKind.Thought);
        Assert.Contains(steps, s => s.Kind == RunStepKind.Final && s.Content == "ma réponse");
    }

    [Fact]
    public async Task ExecuteAsync_invokes_onStep_for_tool_activity()
    {
        var callCount = 0;
        var steps = new List<(RunStepKind Kind, string? Tool)>();
        var (loop, registry, _) = CreateLoop(_ =>
        {
            callCount++;
            return callCount == 1
                ? AIResponse.WithToolCalls(new[] { Call("echo", ("text", "x")) })
                : AIResponse.Text("done");
        });
        registry.Register(new TestTool("echo", "echoes"));

        await loop.ExecuteAsync("goal", CreateContext(), null, "model", new AgentContext("goal"), false,
            onStep: (kind, _, tool, _, _) => steps.Add((kind, tool)));

        Assert.Contains(steps, s => s.Kind == RunStepKind.ToolStarted && s.Tool == "echo");
        Assert.Contains(steps, s => s.Kind == RunStepKind.ToolCompleted && s.Tool == "echo");
    }

    [Fact]
    public async Task ExecuteAsync_truncates_tool_calls_per_turn()
    {
        var callCount = 0;
        var executed = new Dictionary<string, int>();
        var (loop, registry, _) = CreateLoop(_ =>
        {
            callCount++;
            if (callCount == 1)
                return AIResponse.WithToolCalls(new[]
                {
                    Call("echo1"), Call("echo2"), Call("echo3"), Call("echo4"), Call("echo5"), Call("echo6")
                });
            return AIResponse.Text("done");
        });
        for (var i = 1; i <= 6; i++)
        {
            var n = i;
            registry.Register(new TestTool($"echo{n}", "echoes", handler: (_, _) => { lock (executed) executed[$"echo{n}"] = 1; return Task.FromResult(ToolResult.Succeeded("ok")); }));
        }

        var result = await loop.ExecuteAsync("goal", CreateContext(), null, "model", new AgentContext("goal"), false);

        Assert.True(result.Success);
        Assert.Equal(4, executed.Count);
        Assert.Equal(0, executed.GetValueOrDefault("echo5"));
        Assert.Equal(0, executed.GetValueOrDefault("echo6"));
    }

    [Fact]
    public async Task ExecuteAsync_saves_important_tool_result_to_memory()
    {
        var callCount = 0;
        var (loop, registry, memory) = CreateLoop(_ =>
        {
            callCount++;
            return callCount == 1
                ? AIResponse.WithToolCalls(new[] { Call("backup", ("target", "c")) })
                : AIResponse.Text("sauvegarde effectuée");
        });
        registry.Register(new TestTool("backup", "backup", handler: (_, _) => Task.FromResult(ToolResult.Succeeded("Important: sauvegarde terminée à midi"))));

        await loop.ExecuteAsync("goal", CreateContext(), null, "model", new AgentContext("goal"), false);

        var entries = await memory.SearchAsync(new MemoryQuery { TextSearch = "sauvegarde" });
        Assert.Single(entries);
    }

    [Fact]
    public async Task ExecuteAsync_surfaces_tool_failure_in_conversation()
    {
        var callCount = 0;
        var (loop, registry, _) = CreateLoop(_ =>
        {
            callCount++;
            return callCount == 1
                ? AIResponse.WithToolCalls(new[] { Call("broken") })
                : AIResponse.Text("recovered");
        });
        registry.Register(new TestTool("broken", "fails", handler: (_, _) => Task.FromResult(ToolResult.Failed("machin cassé"))));

        var result = await loop.ExecuteAsync("goal", CreateContext(), null, "model", new AgentContext("goal"), false);
        Assert.True(result.Success);
        Assert.Equal("recovered", result.FinalResponse);
    }

    [Fact]
    public async Task ExecuteAsync_cancellation_returns_failed_with_reason()
    {
        var (loop, _, _) = CreateLoop(_ => AIResponse.Text("x"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await loop.ExecuteAsync("goal", CreateContext(), null, "model", new AgentContext("goal"), false, cancellationToken: cts.Token);
        Assert.False(result.Success);
        Assert.Equal("Cancelled", result.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_empty_final_response_prompts_continuation()
    {
        var callCount = 0;
        var (loop, _, _) = CreateLoop(_ =>
        {
            callCount++;
            return callCount == 1 ? AIResponse.Text(string.Empty) : AIResponse.Text("voilà");
        });
        var result = await loop.ExecuteAsync("goal", CreateContext(), null, "model", new AgentContext("goal"), false);
        Assert.True(result.Success);
        Assert.Equal("voilà", result.FinalResponse);
        Assert.Equal(2, result.Iterations);
    }

    [Fact]
    public async Task ExecuteAsync_records_turn_for_tool_iteration()
    {
        var callCount = 0;
        var (loop, registry, _) = CreateLoop(_ =>
        {
            callCount++;
            return callCount == 1
                ? AIResponse.WithToolCalls(new[] { Call("echo") })
                : AIResponse.Text("final");
        });
        registry.Register(new TestTool("echo", "echoes"));

        var result = await loop.ExecuteAsync("goal", CreateContext(), null, "model", new AgentContext("goal"), false);

        Assert.Equal(2, result.Turns.Count);
        Assert.Single(result.Turns[0].ToolCalls);
        Assert.True(result.Turns[0].Outcomes.Count == 1);
        Assert.Equal("final", result.Turns[1].FinalResponse);
        Assert.True(result.Turns[1].Success);
    }
}
