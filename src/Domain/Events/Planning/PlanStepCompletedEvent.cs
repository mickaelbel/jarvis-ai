using JarvisAI.Domain.Events;

namespace JarvisAI.Domain.Events.Planning;

public sealed class PlanStepCompletedEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(PlanStepCompletedEvent);
    public Guid CorrelationId { get; }

    public Guid PlanId { get; }
    public int StepIndex { get; }
    public string Action { get; }
    public bool Success { get; }
    public string? Result { get; }
    public TimeSpan Duration { get; }

    public PlanStepCompletedEvent(Guid planId, int stepIndex, string action, bool success, string? result, TimeSpan duration, Guid correlationId)
    {
        PlanId = planId;
        StepIndex = stepIndex;
        Action = action;
        Success = success;
        Result = result;
        Duration = duration;
        CorrelationId = correlationId;
    }
}
