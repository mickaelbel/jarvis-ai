using JarvisAI.Application.Agents;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Commands;

public sealed class CommandRouter : ICommandRouter
{
    private readonly ILogger<CommandRouter> _logger;
    private readonly List<(ICommand Command, ICommandHandler Handler)> _routes = new();

    public CommandRouter(ILogger<CommandRouter> logger)
    {
        _logger = logger;
    }

    public void Register(ICommand command, ICommandHandler handler)
    {
        _routes.Add((command, handler));
        _logger.LogDebug("[CommandRouter] Registered command: {CommandName} ({Description})", command.Name, command.Description);
    }

    public async Task<CommandResult> RouteAsync(AgentContext context, CancellationToken cancellationToken = default)
    {
        var input = context.CommandText.Trim();

        _logger.LogDebug("[CommandRouter] Routing input: \"{Input}\"", input);

        foreach (var (command, handler) in _routes)
        {
            if (command.CanHandle(input))
            {
                _logger.LogInformation("[CommandRouter] Matched command: {CommandName}", command.Name);
                return await handler.HandleAsync(command, context, cancellationToken);
            }
        }

        _logger.LogWarning("[CommandRouter] No command matched input: \"{Input}\"", input);
        return CommandResult.Failed($"No command found for: {input}");
    }

    public IReadOnlyList<(ICommand Command, ICommandHandler Handler)> GetAll()
        => _routes.AsReadOnly();
}
