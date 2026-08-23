using JarvisAI.Domain.Events;

namespace JarvisAI.Domain.Events.Agents;

public sealed class AgentCommandReceivedEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(AgentCommandReceivedEvent);
    public Guid CorrelationId { get; }

    public string CommandText { get; }
    public string? Source { get; }
    public Dictionary<string, object> Metadata { get; }

    public AgentCommandReceivedEvent(string commandText, Guid correlationId, string? source = null, Dictionary<string, object>? metadata = null)
    {
        CommandText = commandText;
        CorrelationId = correlationId;
        Source = source;
        Metadata = metadata ?? new();
    }
}
