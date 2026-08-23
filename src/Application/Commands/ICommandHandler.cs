using JarvisAI.Application.Agents;

namespace JarvisAI.Application.Commands;

public interface ICommandHandler
{
    Task<CommandResult> HandleAsync(ICommand command, AgentContext context, CancellationToken cancellationToken = default);
}
