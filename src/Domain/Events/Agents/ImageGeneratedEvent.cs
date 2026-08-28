using JarvisAI.Domain.Events;

namespace JarvisAI.Domain.Events.Agents;

public sealed class ImageGeneratedEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(ImageGeneratedEvent);
    public Guid CorrelationId { get; }

    public string Prompt { get; }
    public string DataUrl { get; }
    public string FilePath { get; }

    public ImageGeneratedEvent(string prompt, string dataUrl, string filePath, Guid correlationId)
    {
        Prompt = prompt;
        DataUrl = dataUrl;
        FilePath = filePath;
        CorrelationId = correlationId;
    }
}
