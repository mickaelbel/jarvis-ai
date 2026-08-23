using JarvisAI.Application.Agents;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Application.Tools;

public sealed class PendingToolCall
{
    public string Id { get; }
    public string Name { get; }
    public IReadOnlyDictionary<string, string> Arguments { get; }

    public PendingToolCall(string name, IReadOnlyDictionary<string, string> arguments, string? id = null)
    {
        Name = name;
        Arguments = arguments ?? new Dictionary<string, string>();
        Id = id ?? Guid.NewGuid().ToString("N")[..12];
    }
}

public sealed class ToolExecutionOutcome
{
    public string ToolName { get; }
    public bool Success { get; }
    public string Output { get; }
    public string? ErrorMessage { get; }
    public TimeSpan Duration { get; }

    public ToolExecutionOutcome(string toolName, bool success, string output, string? errorMessage, TimeSpan duration)
    {
        ToolName = toolName;
        Success = success;
        Output = output;
        ErrorMessage = errorMessage;
        Duration = duration;
    }

    public static ToolExecutionOutcome FromResult(string toolName, ToolResult result, TimeSpan duration)
        => new(toolName, result.Success, result.Output, result.ErrorMessage, duration);
}

public interface IParallelToolExecutor
{
    Task<IReadOnlyList<ToolExecutionOutcome>> ExecuteAsync(
        IReadOnlyList<PendingToolCall> calls,
        AgentContext context,
        bool allowParallel,
        CancellationToken cancellationToken = default);

    bool CanRunInParallel(string toolName);
}

public sealed class ParallelToolExecutor : IParallelToolExecutor
{
    private static readonly HashSet<string> ParallelSafeTools = new(StringComparer.Ordinal)
    {
        "system_info", "date_time", "memory", "clipboard", "ui_elements", "vision"
    };

    private readonly IToolExecutor _toolExecutor;
    private readonly IToolRegistry _toolRegistry;
    private readonly IRetryPolicy _retryPolicy;
    private readonly ILogger<ParallelToolExecutor> _logger;

    public ParallelToolExecutor(
        IToolExecutor toolExecutor,
        IToolRegistry toolRegistry,
        IRetryPolicy retryPolicy,
        ILogger<ParallelToolExecutor> logger)
    {
        _toolExecutor = toolExecutor;
        _toolRegistry = toolRegistry;
        _retryPolicy = retryPolicy;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ToolExecutionOutcome>> ExecuteAsync(
        IReadOnlyList<PendingToolCall> calls,
        AgentContext context,
        bool allowParallel,
        CancellationToken cancellationToken = default)
    {
        if (calls is null || calls.Count == 0)
            return Array.Empty<ToolExecutionOutcome>();

        var canRunParallel = allowParallel && calls.All(c => CanRunInParallel(c.Name));
        if (canRunParallel && calls.Count > 1)
        {
            _logger.LogInformation("[ParallelToolExecutor] Running {Count} tool calls in parallel", calls.Count);
            var tasks = calls.Select(call => ExecuteSingleAsync(call, context, cancellationToken)).ToArray();
            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        _logger.LogInformation("[ParallelToolExecutor] Running {Count} tool calls sequentially", calls.Count);
        var sequential = new List<ToolExecutionOutcome>(calls.Count);
        foreach (var call in calls)
            sequential.Add(await ExecuteSingleAsync(call, context, cancellationToken));
        return sequential;
    }

    public bool CanRunInParallel(string toolName)
    {
        if (ParallelSafeTools.Contains(toolName))
            return true;

        var tool = _toolRegistry.GetByName(toolName);
        if (tool is null) return false;

        return tool.RiskLevel == Domain.Security.SecurityRiskLevel.Low;
    }

    private async Task<ToolExecutionOutcome> ExecuteSingleAsync(PendingToolCall call, AgentContext parentContext, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var toolContext = new AgentContext(
            call.Name,
            source: "agent_orchestrator",
            new Dictionary<string, object>
            {
                ["toolCallId"] = call.Id,
                ["arguments"] = call.Arguments
            });

        try
        {
            var result = await _retryPolicy.ExecuteAsync(
                ct => _toolExecutor.ExecuteAsync(call.Name, toolContext, ct),
                $"tool:{call.Name}",
                cancellationToken: cancellationToken);

            sw.Stop();
            return ToolExecutionOutcome.FromResult(call.Name, result, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new ToolExecutionOutcome(call.Name, false, string.Empty, "Cancelled", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "[ParallelToolExecutor] Tool {ToolName} failed", call.Name);
            return new ToolExecutionOutcome(call.Name, false, string.Empty, ex.Message, sw.Elapsed);
        }
    }
}
