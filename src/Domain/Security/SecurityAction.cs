namespace JarvisAI.Domain.Security;

public sealed class SecurityAction
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public SecurityRiskLevel RiskLevel { get; set; }
    public PermissionLevel RequiredPermission { get; set; } = PermissionLevel.User;
    public bool RequiresConfirmation { get; set; }
    public TimeSpan? Timeout { get; set; }

    public static SecurityAction LowRisk(string name, string description) => new()
    {
        Name = name,
        Description = description,
        RiskLevel = SecurityRiskLevel.Low,
        RequiresConfirmation = false
    };

    public static SecurityAction MediumRisk(string name, string description) => new()
    {
        Name = name,
        Description = description,
        RiskLevel = SecurityRiskLevel.Medium,
        RequiresConfirmation = false
    };

    public static SecurityAction HighRisk(string name, string description) => new()
    {
        Name = name,
        Description = description,
        RiskLevel = SecurityRiskLevel.High,
        RequiresConfirmation = true,
        Timeout = TimeSpan.FromSeconds(30)
    };
}
