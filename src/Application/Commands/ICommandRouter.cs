using JarvisAI.Application.Agents;

namespace JarvisAI.Application.Commands;

public interface ICommandRouter
{
    void Register(ICommand command, ICommandHandler handler);
    Task<CommandResult> RouteAsync(AgentContext context, CancellationToken cancellationToken = default);
    IReadOnlyList<(ICommand Command, ICommandHandler Handler)> GetAll();
}
