using JarvisAI.Domain.Events;

namespace JarvisAI.Domain.Events.Planning;

public sealed class PlanCreatedEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(PlanCreatedEvent);
    public Guid CorrelationId { get; }

    public Guid PlanId { get; }
    public string Goal { get; }
    public int StepCount { get; }

    public PlanCreatedEvent(Guid planId, string goal, int stepCount, Guid correlationId)
    {
        PlanId = planId;
        Goal = goal;
        StepCount = stepCount;
        CorrelationId = correlationId;
    }
}
