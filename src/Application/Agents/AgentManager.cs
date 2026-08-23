using Microsoft.Extensions.DependencyInjection;

namespace JarvisAI.Application.Agents;

public sealed class AgentManager
{
    private readonly IServiceProvider _serviceProvider;
    private readonly List<IAgent> _agents = new();

    public AgentManager(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IReadOnlyList<IAgent> Agents => _agents.AsReadOnly();

    public void RegisterAgent<T>() where T : class, IAgent
    {
        var agent = _serviceProvider.GetRequiredService<T>();
        _agents.Add(agent);
    }

    public void RegisterAgent(IAgent agent)
    {
        _agents.Add(agent);
    }

    public async Task<AgentResult> ProcessAsync(AgentContext context, CancellationToken cancellationToken = default)
    {
        if (_agents.Count == 0)
            throw new InvalidOperationException("No agents registered. Call RegisterAgent first.");

        var agent = _agents[0];
        return await agent.ProcessAsync(context, cancellationToken);
    }
}
