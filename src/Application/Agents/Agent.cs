using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Commands;
using JarvisAI.Domain.Events.Agents;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Agents;

public sealed class Agent : IAgent
{
    public string Name => "JarvisAgent";

    private readonly ICommandRouter _commandRouter;
    private readonly IEventBus _eventBus;
    private readonly ILogger<Agent> _logger;

    public Agent(ICommandRouter commandRouter, IEventBus eventBus, ILogger<Agent> logger)
    {
        _commandRouter = commandRouter;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<AgentResult> ProcessAsync(AgentContext context, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation("[Agent] Processing command: \"{Command}\" (CorrelationId={CorrelationId})",
            context.CommandText, context.CorrelationId);

        await _eventBus.PublishAsync(
            new AgentCommandReceivedEvent(context.CommandText, context.CorrelationId, context.Source, context.Metadata),
            cancellationToken);

        try
        {
            var result = await _commandRouter.RouteAsync(context, cancellationToken);
            sw.Stop();

            var agentResult = result.Success
                ? AgentResult.Succeeded(result.Response, result.ToolUsed, sw.Elapsed)
                : AgentResult.Failed(result.Response, new InvalidOperationException(result.ErrorMessage ?? "Command failed"), sw.Elapsed);

            await _eventBus.PublishAsync(
                new AgentResponseReadyEvent(agentResult.Response, agentResult.Success, context.CorrelationId),
                cancellationToken);

            _logger.LogInformation("[Agent] Command processed in {Elapsed}ms - Success={Success} - Tool={Tool}",
                sw.ElapsedMilliseconds, agentResult.Success, agentResult.ToolUsed ?? "none");

            return agentResult;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[Agent] Error processing command (CorrelationId={CorrelationId})", context.CorrelationId);

            var failResult = AgentResult.Failed($"Error: {ex.Message}", ex, sw.Elapsed);

            await _eventBus.PublishAsync(
                new AgentResponseReadyEvent(failResult.Response, false, context.CorrelationId),
                cancellationToken);

            return failResult;
        }
    }
}
