using JarvisAI.Domain.Events;

namespace JarvisAI.Domain.Events.Agents;

public sealed class AgentToolExecutedEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(AgentToolExecutedEvent);
    public Guid CorrelationId { get; }

    public string ToolName { get; }
    public bool Success { get; }
    public string? ErrorMessage { get; }
    public string? Result { get; }
    public TimeSpan Duration { get; }

    public AgentToolExecutedEvent(string toolName, bool success, Guid correlationId, TimeSpan duration, string? errorMessage = null, string? result = null)
    {
        ToolName = toolName;
        Success = success;
        CorrelationId = correlationId;
        Duration = duration;
        ErrorMessage = errorMessage;
        Result = result;
    }
}
