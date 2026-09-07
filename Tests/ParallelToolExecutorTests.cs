using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class ParallelToolExecutorTests
{
    private static (ParallelToolExecutor executor, FakeToolExecutor fake) Create(Func<string, ToolResult>? handler = null)
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var fake = new FakeToolExecutor(handler);
        var retryPolicy = new RetryPolicy();
        return (new ParallelToolExecutor(fake, registry, retryPolicy, NullLogger<ParallelToolExecutor>.Instance), fake);
    }

    private static AgentContext Context() => new("goal", "test");

    [Fact]
    public async Task ExecuteAsync_empty_calls_returns_empty()
    {
        var (executor, _) = Create();
        var result = await executor.ExecuteAsync(Array.Empty<PendingToolCall>(), Context(), allowParallel: true);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteAsync_null_calls_returns_empty()
    {
        var (executor, _) = Create();
        var result = await executor.ExecuteAsync(null!, Context(), allowParallel: true);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteAsync_runs_sequentially_when_not_allowed()
    {
        var (executor, fake) = Create();
        var calls = new[] { new PendingToolCall("system_info", new Dictionary<string, string>()), new PendingToolCall("date_time", new Dictionary<string, string>()) };
        var result = await executor.ExecuteAsync(calls, Context(), allowParallel: false);
        Assert.Equal(new[] { "system_info", "date_time" }, fake.Calls);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ExecuteAsync_runs_parallel_for_safe_tools()
    {
        var (executor, fake) = Create();
        var calls = new[] { new PendingToolCall("system_info", new Dictionary<string, string>()), new PendingToolCall("date_time", new Dictionary<string, string>()) };
        var result = await executor.ExecuteAsync(calls, Context(), allowParallel: true);
        Assert.Equal(2, result.Count);
        Assert.All(result, o => Assert.True(o.Success));
        Assert.Equal(2, fake.Calls.Count);
    }

    [Fact]
    public async Task ExecuteAsync_parallel_execution_is_faster_than_sequential()
    {
        var (executor, fake) = Create();
        fake.Delay = TimeSpan.FromMilliseconds(20);
        var calls = Enumerable.Range(0, 10)
            .Select(_ => new PendingToolCall("system_info", new Dictionary<string, string>()))
            .ToList();

        var parallelStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await executor.ExecuteAsync(calls, Context(), allowParallel: true);
        parallelStopwatch.Stop();

        var sequentialStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await executor.ExecuteAsync(calls, Context(), allowParallel: false);
        sequentialStopwatch.Stop();

        Assert.True(parallelStopwatch.Elapsed < sequentialStopwatch.Elapsed);
    }

    [Fact]
    public async Task ExecuteAsync_mixed_risk_runs_sequentially()
    {
        var (executor, fake) = Create();
        var calls = new[] { new PendingToolCall("system_info", new Dictionary<string, string>()), new PendingToolCall("terminal", new Dictionary<string, string>()) };
        var result = await executor.ExecuteAsync(calls, Context(), allowParallel: true);
        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "system_info", "terminal" }, fake.Calls);
    }

    [Fact]
    public void CanRunInParallel_returns_true_for_safe_tools()
    {
        var (executor, _) = Create();
        Assert.True(executor.CanRunInParallel("system_info"));
        Assert.True(executor.CanRunInParallel("date_time"));
        Assert.True(executor.CanRunInParallel("memory"));
    }

    [Fact]
    public void CanRunInParallel_returns_true_for_low_risk_registered_tool()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(new TestTool("safe_tool", "low risk", "test", SecurityRiskLevel.Low));
        var executor = new ParallelToolExecutor(new FakeToolExecutor(), registry, new RetryPolicy(), NullLogger<ParallelToolExecutor>.Instance);
        Assert.True(executor.CanRunInParallel("safe_tool"));
    }

    [Fact]
    public void CanRunInParallel_returns_false_for_high_risk_registered_tool()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(new TestTool("risky_tool", "high risk", "test", SecurityRiskLevel.High));
        var executor = new ParallelToolExecutor(new FakeToolExecutor(), registry, new RetryPolicy(), NullLogger<ParallelToolExecutor>.Instance);
        Assert.False(executor.CanRunInParallel("risky_tool"));
    }

    [Fact]
    public void CanRunInParallel_returns_false_for_unknown_tool()
    {
        var (executor, _) = Create();
        Assert.False(executor.CanRunInParallel("ghost_tool"));
    }

    [Fact]
    public async Task ExecuteAsync_captures_tool_failure_result()
    {
        var (executor, _) = Create(_ => ToolResult.Failed("something broke"));
        var result = await executor.ExecuteAsync(new[] { new PendingToolCall("system_info", new Dictionary<string, string>()) }, Context(), allowParallel: true);
        var outcome = Assert.Single(result);
        Assert.False(outcome.Success);
        Assert.Equal("something broke", outcome.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_captures_tool_exception()
    {
        var (executor, _) = Create(_ => throw new InvalidOperationException("explosion"));
        var result = await executor.ExecuteAsync(new[] { new PendingToolCall("system_info", new Dictionary<string, string>()) }, Context(), allowParallel: true);
        var outcome = Assert.Single(result);
        Assert.False(outcome.Success);
        Assert.Contains("explosion", outcome.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_returns_cancelled_outcome_on_cancellation()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var fake = new FakeToolExecutor(_ => { throw new TaskCanceledException(); });
        var executor = new ParallelToolExecutor(fake, registry, new RetryPolicy(new RetryPolicyOptions { MaxRetries = 0 }), NullLogger<ParallelToolExecutor>.Instance);
        var result = await executor.ExecuteAsync(new[] { new PendingToolCall("system_info", new Dictionary<string, string>()) }, Context(), allowParallel: true);
        var outcome = Assert.Single(result);
        Assert.False(outcome.Success);
    }

    [Fact]
    public async Task ExecuteAsync_preserves_outcome_order_for_sequential()
    {
        var (executor, _) = Create();
        var calls = new[] { new PendingToolCall("one", new Dictionary<string, string>()), new PendingToolCall("two", new Dictionary<string, string>()) };
        var result = await executor.ExecuteAsync(calls, Context(), allowParallel: false);
        Assert.Equal(new[] { "one", "two" }, result.Select(o => o.ToolName));
    }

    [Fact]
    public async Task ExecuteAsync_single_call_runs_even_without_parallel()
    {
        var (executor, fake) = Create();
        var result = await executor.ExecuteAsync(new[] { new PendingToolCall("date_time", new Dictionary<string, string>()) }, Context(), allowParallel: false);
        Assert.Single(result);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public void PendingToolCall_generates_id_when_missing()
    {
        var call = new PendingToolCall("echo", new Dictionary<string, string>());
        Assert.False(string.IsNullOrEmpty(call.Id));
    }
}

internal class FakeToolExecutor : IToolExecutor
{
    private readonly Func<string, ToolResult>? _handler;
    public List<string> Calls { get; } = new();
    public TimeSpan? Delay { get; set; }

    public FakeToolExecutor(Func<string, ToolResult>? handler = null) => _handler = handler;

    public async Task<ToolResult> ExecuteAsync(string toolName, AgentContext context, CancellationToken ct = default)
    {
        Calls.Add(toolName);
        if (Delay is { } delay && delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        return _handler?.Invoke(toolName) ?? ToolResult.Succeeded($"{toolName} executed");
    }
}

internal class TestTool : ITool
{
    public string Name { get; }
    public string Description { get; }
    public string Category { get; }
    public SecurityRiskLevel RiskLevel { get; }
    public bool IsAvailable => true;
    public bool McpExpose => false;
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    private readonly Func<AgentContext, IReadOnlyDictionary<string, string>, CancellationToken, Task<ToolResult>>? _handler;

    public TestTool(string name, string description = "", string category = "test", SecurityRiskLevel riskLevel = SecurityRiskLevel.Low,
        Func<AgentContext, IReadOnlyDictionary<string, string>, CancellationToken, Task<ToolResult>>? handler = null)
    {
        Name = name;
        Description = description;
        Category = category;
        RiskLevel = riskLevel;
        _handler = handler;
    }

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
        => _handler is not null ? _handler(context, parameters, ct) : Task.FromResult(ToolResult.Succeeded("ok"));
}
