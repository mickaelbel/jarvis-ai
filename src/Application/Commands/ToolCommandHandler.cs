using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Events.Agents;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Commands;

public sealed class ToolCommandHandler : ICommandHandler
{
    private readonly IToolExecutor _toolExecutor;
    private readonly ILogger<ToolCommandHandler> _logger;

    public ToolCommandHandler(IToolExecutor toolExecutor, ILogger<ToolCommandHandler> logger)
    {
        _toolExecutor = toolExecutor;
        _logger = logger;
    }

    public async Task<CommandResult> HandleAsync(ICommand command, AgentContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("[ToolCommandHandler] Handling command: {CommandName}", command.Name);
        var toolName = command.ToolName ?? command.Name;
        var result = await _toolExecutor.ExecuteAsync(toolName, context, cancellationToken);

        if (result.Success)
            return CommandResult.Succeeded(result.Output, toolName);

        return CommandResult.Failed(result.ErrorMessage ?? "Tool execution failed");
    }
}
