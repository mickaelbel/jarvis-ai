using JarvisAI.Domain.Events;

namespace JarvisAI.Domain.Events.Agents;

/// <summary>Publié quand un run d'agent démarre.</summary>
public sealed class AgentStartedEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(AgentStartedEvent);
    public Guid CorrelationId { get; }

    public Guid RunId { get; }
    public string Goal { get; }
    public string AgentKind { get; }

    public AgentStartedEvent(Guid runId, string goal, string agentKind, Guid correlationId)
    {
        RunId = runId;
        Goal = goal;
        AgentKind = agentKind;
        CorrelationId = correlationId;
    }
}

/// <summary>Publié quand un run d'agent se termine (réussite ou échec).</summary>
public sealed class AgentCompletedEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(AgentCompletedEvent);
    public Guid CorrelationId { get; }

    public Guid RunId { get; }
    public bool Success { get; }
    public string? Reason { get; }
    public int Iterations { get; }

    public AgentCompletedEvent(Guid runId, bool success, string? reason, int iterations, Guid correlationId)
    {
        RunId = runId;
        Success = success;
        Reason = reason;
        Iterations = iterations;
        CorrelationId = correlationId;
    }
}

/// <summary>Publié quand une observation de l'état est produite.</summary>
public sealed class AgentObservationReceivedEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(AgentObservationReceivedEvent);
    public Guid CorrelationId { get; }

    public Guid RunId { get; }
    public string Summary { get; }

    public AgentObservationReceivedEvent(Guid runId, string summary, Guid correlationId)
    {
        RunId = runId;
        Summary = summary;
        CorrelationId = correlationId;
    }
}
