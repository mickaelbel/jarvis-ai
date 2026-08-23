using JarvisAI.Domain.Events;

namespace JarvisAI.Domain.Events.Memory;

public sealed class MemoryRetrievedEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(MemoryRetrievedEvent);
    public Guid CorrelationId { get; }

    public string Key { get; }
    public bool Found { get; }

    public MemoryRetrievedEvent(string key, bool found, Guid correlationId)
    {
        Key = key;
        Found = found;
        CorrelationId = correlationId;
    }
}
