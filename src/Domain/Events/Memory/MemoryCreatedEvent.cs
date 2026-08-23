using JarvisAI.Domain.Events;

namespace JarvisAI.Domain.Events.Memory;

public sealed class MemoryCreatedEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(MemoryCreatedEvent);
    public Guid CorrelationId { get; }

    public string Key { get; }
    public string Category { get; }

    public MemoryCreatedEvent(string key, string category, Guid correlationId)
    {
        Key = key;
        Category = category;
        CorrelationId = correlationId;
    }
}
