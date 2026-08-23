using JarvisAI.Domain.Events;

namespace JarvisAI.Domain.Events.Planning;

public sealed class PlanFailedEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(PlanFailedEvent);
    public Guid CorrelationId { get; }

    public Guid PlanId { get; }
    public string? ErrorMessage { get; }
    public int FailedStepIndex { get; }

    public PlanFailedEvent(Guid planId, string? errorMessage, int failedStepIndex, Guid correlationId)
    {
        PlanId = planId;
        ErrorMessage = errorMessage;
        FailedStepIndex = failedStepIndex;
        CorrelationId = correlationId;
    }
}
