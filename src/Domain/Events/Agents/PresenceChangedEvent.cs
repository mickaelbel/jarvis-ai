using JarvisAI.Domain.Events;

namespace JarvisAI.Domain.Events.Agents;

/// <summary>Événement publié quand la présence de l'utilisateur change (ping téléphone).</summary>
public sealed class PresenceChangedEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(PresenceChangedEvent);
    public Guid CorrelationId => EventId;

    /// <summary>"presence_return" ou "presence_departure".</summary>
    public string Kind { get; }
    public string Detail { get; }

    public PresenceChangedEvent(string kind, string detail)
    {
        Kind = kind;
        Detail = detail;
    }
}
