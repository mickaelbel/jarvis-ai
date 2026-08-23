namespace JarvisAI.Application.Security;

public sealed class ConfirmationRequest
{
    public string ToolName { get; }
    public string Description { get; }
    public string RiskLevel { get; }
    public Guid CorrelationId { get; }
    public Dictionary<string, string> Parameters { get; }

    public ConfirmationRequest(string toolName, string description, string riskLevel, Guid correlationId, Dictionary<string, string>? parameters = null)
    {
        ToolName = toolName;
        Description = description;
        RiskLevel = riskLevel;
        CorrelationId = correlationId;
        Parameters = parameters ?? new();
    }
}
