namespace JarvisAI.Application.Agents;

public interface IAgent
{
    string Name { get; }
    Task<AgentResult> ProcessAsync(AgentContext context, CancellationToken cancellationToken = default);
}
