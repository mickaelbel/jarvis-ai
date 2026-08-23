using JarvisAI.Domain.Events;

namespace JarvisAI.Domain.Events.Agents;

public sealed class AgentToolStartedEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(AgentToolStartedEvent);
    public Guid CorrelationId { get; }

    public string ToolName { get; }
    public IReadOnlyDictionary<string, string> Arguments { get; }

    public AgentToolStartedEvent(string toolName, IReadOnlyDictionary<string, string> arguments, Guid correlationId)
    {
        ToolName = toolName;
        Arguments = arguments;
        CorrelationId = correlationId;
    }
}
