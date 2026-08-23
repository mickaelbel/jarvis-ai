using JarvisAI.Domain.Events;

namespace JarvisAI.Domain.Security;

public sealed class SecurityEvent : IEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
    public string EventType => nameof(SecurityEvent);
    public Guid CorrelationId { get; }

    public string Action { get; }
    public string ToolName { get; }
    public SecurityRiskLevel RiskLevel { get; }
    public bool UserConfirmed { get; }
    public string Result { get; }
    public string? Details { get; }

    public SecurityEvent(string action, string toolName, SecurityRiskLevel riskLevel, bool userConfirmed, string result, Guid correlationId, string? details = null)
    {
        Action = action;
        ToolName = toolName;
        RiskLevel = riskLevel;
        UserConfirmed = userConfirmed;
        Result = result;
        CorrelationId = correlationId;
        Details = details;
    }
}
