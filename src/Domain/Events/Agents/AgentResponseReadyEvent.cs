using JarvisAI.Domain.Events;

namespace JarvisAI.Domain.Events.Agents;

public sealed class AgentResponseReadyEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(AgentResponseReadyEvent);
    public Guid CorrelationId { get; }

    public string Response { get; }
    public bool Success { get; }

    public AgentResponseReadyEvent(string response, bool success, Guid correlationId)
    {
        Response = response;
        Success = success;
        CorrelationId = correlationId;
    }
}
