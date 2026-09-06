using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Agents;
using JarvisAI.Domain.Events.Agents;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Tools;

public sealed class ToolExecutor : IToolExecutor
{
    private readonly IToolRegistry _registry;
    private readonly IEventBus _eventBus;
    private readonly ISecurityManager? _securityManager;
    private readonly ILogger<ToolExecutor> _logger;
    private readonly ToolTimeoutOptions _timeoutOptions;

    public ToolExecutor(IToolRegistry registry, IEventBus eventBus, ILogger<ToolExecutor> logger,
        ISecurityManager? securityManager = null, ToolTimeoutOptions? timeoutOptions = null)
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

        const int maxRetries = 2;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var timeout = _timeoutOptions.GetTimeout(toolName);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);

                var toolTask = tool.ExecuteAsync(context, toolArgs, timeoutCts.Token);
                var result = await toolTask;
                sw.Stop();

                _logger.LogInformation("[ToolExecutor] Tool {ToolName} executed in {Elapsed}ms (attempt {Attempt}) - Success={Success} {ResultPreview}",
                    toolName, sw.ElapsedMilliseconds, attempt + 1, result.Success,
                    result.Success ? Truncate(result.Output, 200) : result.ErrorMessage);

                await _eventBus.PublishAsync(
                    new AgentToolExecutedEvent(toolName, true, context.CorrelationId, sw.Elapsed, result: Truncate(result.Output, 200)),
                    cancellationToken);

                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                sw.Stop();
                var timeout = _timeoutOptions.GetTimeout(toolName);
                var msg = $"Tool '{toolName}' timed out after {timeout.TotalSeconds:F0}s";
                _logger.LogWarning("[ToolExecutor] {Message}", msg);
                return ToolResult.Failed(msg);
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransient(ex))
            {
                _logger.LogWarning(ex, "[ToolExecutor] Transient error on {ToolName}, attempt {Attempt}/{MaxRetries}, retrying in {Delay}ms",
                    toolName, attempt + 1, maxRetries, 500 * (attempt + 1));
                await Task.Delay(500 * (attempt + 1), cancellationToken);
            }
            catch (Exception ex) when (attempt == maxRetries)
            {
                sw.Stop();
                _logger.LogError(ex, "[ToolExecutor] Tool {ToolName} failed after {Elapsed}ms and {Attempts} attempts",
                    toolName, sw.ElapsedMilliseconds, maxRetries + 1);
                await _eventBus.PublishAsync(
                    new AgentToolExecutedEvent(toolName, false, context.CorrelationId, sw.Elapsed, ex.Message),
                    cancellationToken);
                return ToolResult.Failed(ex.Message);
            }
        }

        sw.Stop();
        return ToolResult.Failed($"Tool '{toolName}' failed after {maxRetries + 1} attempts");
    }

    private static IReadOnlyDictionary<string, string> ExtractToolArguments(AgentContext context)
    {
        if (context.Metadata.TryGetValue("arguments", out var argsObj))
        {
            if (argsObj is IReadOnlyDictionary<string, string> stringArgs)
                return stringArgs;
            if (argsObj is IReadOnlyDictionary<string, object> objArgs)
                return objArgs.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
        }

        return new Dictionary<string, string>();
    }

    private static bool IsTransient(Exception ex) =>
        ex is System.Net.Http.HttpRequestException
        or System.IO.IOException
        or System.Net.Sockets.SocketException;

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength] + "...";
    }
}
