using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Agents;
using JarvisAI.Domain.Events.Agents;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class ToolExecutor : IToolExecutor
{
    private readonly IToolRegistry _registry;
    private readonly IEventBus _eventBus;
    private readonly ISecurityManager? _securityManager;
    private readonly ILogger<ToolExecutor> _logger;
    private readonly ToolTimeoutOptions _timeoutOptions;

    public ToolExecutor(IToolRegistry registry, IEventBus eventBus, ILogger<ToolExecutor> logger, ISecurityManager? securityManager = null, ToolTimeoutOptions? timeoutOptions = null)
    {
        _registry = registry;
        _eventBus = eventBus;
        _logger = logger;
        _securityManager = securityManager;
        _timeoutOptions = timeoutOptions ?? new ToolTimeoutOptions();
    }

    public async Task<ToolResult> ExecuteAsync(string toolName, AgentContext context, CancellationToken cancellationToken = default)
    {
        var tool = _registry.GetByName(toolName);
        if (tool is null)
        {
            _logger.LogWarning("[ToolExecutor] Tool not found: {ToolName}", toolName);
            return ToolResult.Failed($"Tool not found: {toolName}");
        }

        var toolArgs = ExtractToolArguments(context);

        if (_securityManager != null)
        {
            var isAllowed = await _securityManager.IsToolAllowedAsync(toolName, context.CorrelationId, cancellationToken);
            if (!isAllowed)
            {
                _logger.LogWarning("[ToolExecutor] Tool blocked by security: {ToolName}", toolName);
                return ToolResult.Failed($"Tool '{toolName}' is blocked by security policy");
            }

            var confirmation = await _securityManager.CheckAndConfirmAsync(toolName, context, toolArgs, cancellationToken);
            if (!confirmation.Confirmed)
            {
                _logger.LogWarning("[ToolExecutor] Tool execution denied by user: {ToolName} (Method={Method})", toolName, confirmation.Method);
                return ToolResult.Failed($"Tool '{toolName}' execution denied by user: {confirmation.ResponseText ?? "denied"}");
            }
        }

        var sw = Stopwatch.StartNew();

        _logger.LogInformation("[ToolExecutor] Executing tool: {ToolName} (Risk={RiskLevel}, Args={ArgCount}, CorrelationId={CorrelationId})",
            toolName, tool.RiskLevel, toolArgs.Count, context.CorrelationId);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var kvp in toolArgs)
                _logger.LogDebug("[ToolExecutor] Arg: {Key} = {Value}", kvp.Key, kvp.Value);
        }

        await _eventBus.PublishAsync(
            new AgentToolStartedEvent(toolName, toolArgs, context.CorrelationId),
            cancellationToken);

        try
        {
            var timeout = _timeoutOptions.GetTimeout(toolName);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            Task<ToolResult> toolTask;
            try
            {
                toolTask = tool.ExecuteAsync(context, toolArgs, timeoutCts.Token);
                var result = await toolTask;
                sw.Stop();

                _logger.LogInformation("[ToolExecutor] Tool {ToolName} executed in {Elapsed}ms - Success={Success} {ResultPreview}",
                    toolName, sw.ElapsedMilliseconds, result.Success,
                    result.Success ? Truncate(result.Output, 200) : result.ErrorMessage);

                await _eventBus.PublishAsync(
                    new AgentToolExecutedEvent(toolName, true, context.CorrelationId, sw.Elapsed, result: Truncate(result.Output, 200)),
                    cancellationToken);

                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                sw.Stop();
                var msg = $"Tool '{toolName}' timed out after {timeout.TotalSeconds:F0}s";
                _logger.LogWarning("[ToolExecutor] {Message}", msg);
                return ToolResult.Failed(msg);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();

            _logger.LogError(ex, "[ToolExecutor] Tool {ToolName} failed after {Elapsed}ms",
                toolName, sw.ElapsedMilliseconds);

            await _eventBus.PublishAsync(
                new AgentToolExecutedEvent(toolName, false, context.CorrelationId, sw.Elapsed, ex.Message),
                cancellationToken);

            return ToolResult.Failed(ex.Message);
        }
    }

    private static IReadOnlyDictionary<string, string> ExtractToolArguments(AgentContext context)
    {
        if (context.Metadata.TryGetValue("arguments", out var argsObj) &&
            argsObj is IReadOnlyDictionary<string, string> args)
        {
            return args;
        }

        return new Dictionary<string, string>();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength] + "...";
    }
}
