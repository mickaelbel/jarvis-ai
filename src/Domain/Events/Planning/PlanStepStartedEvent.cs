using JarvisAI.Domain.Events;

namespace JarvisAI.Domain.Events.Planning;

public sealed class PlanStepStartedEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(PlanStepStartedEvent);
    public Guid CorrelationId { get; }

    public Guid PlanId { get; }
    public int StepIndex { get; }
    public string Action { get; }
    public string? ToolName { get; }

    public PlanStepStartedEvent(Guid planId, int stepIndex, string action, string? toolName, Guid correlationId)
    {
        PlanId = planId;
        StepIndex = stepIndex;
        Action = action;
        ToolName = toolName;
        CorrelationId = correlationId;
    }
}
