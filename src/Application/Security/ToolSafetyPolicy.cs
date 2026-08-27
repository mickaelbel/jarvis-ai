using JarvisAI.Domain.Security;

namespace JarvisAI.Application.Security;

/// <summary>
/// Dérive le niveau de sûreté N1/N2/N3 d'un appel d'outil.
/// Le niveau est calculé, jamais stocké : croisement du RiskLevel de l'outil
/// et d'une liste figée d'actions critiques (N3).
/// </summary>
public static class ToolSafetyPolicy
{
    /// <summary>Actions critiques : confirmation obligatoire à chaque fois.</summary>
    private static readonly HashSet<(string Tool, string? Action)> CriticalN3 = new(new StringTupleComparer())
    {
        ("power", "shutdown"),
        ("power", "restart"),
    };

    public static ToolSafetyLevel GetLevel(string toolName, SecurityRiskLevel riskLevel, string? action = null)
    {
        if (ContainsCritical(toolName, action))
            return ToolSafetyLevel.N3;

        if (riskLevel == SecurityRiskLevel.Critical)
            return ToolSafetyLevel.N3;

        return riskLevel switch
        {
            SecurityRiskLevel.Safe => ToolSafetyLevel.N1,
            SecurityRiskLevel.Low => ToolSafetyLevel.N1,
            SecurityRiskLevel.Medium => ToolSafetyLevel.N2,
            SecurityRiskLevel.High => IsKnownSensitiveTool(toolName) ? ToolSafetyLevel.N3 : ToolSafetyLevel.N2,
            _ => ToolSafetyLevel.N2,
        };
    }

    private static bool ContainsCritical(string toolName, string? action)
    {
        foreach (var (tool, criticalAction) in CriticalN3)
        {
            if (!tool.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (criticalAction is null || (action is not null && criticalAction.Equals(action, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    /// <summary>Outils dont toute action destructive reste critique même hors liste figée.</summary>
    private static bool IsKnownSensitiveTool(string toolName)
        => toolName.Equals("power", StringComparison.OrdinalIgnoreCase);

    private sealed class StringTupleComparer : IEqualityComparer<(string Tool, string? Action)>
    {
        public bool Equals((string Tool, string? Action) x, (string Tool, string? Action) y)
            => x.Tool.Equals(y.Tool, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Action, y.Action, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Tool, string? Action) obj)
            => HashCode.Combine(obj.Tool.ToLowerInvariant(), obj.Action?.ToLowerInvariant());
    }
}
