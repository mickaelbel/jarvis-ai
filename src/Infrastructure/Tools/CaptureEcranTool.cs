using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Windows;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>Capture l'écran et retourne le chemin du PNG (base multimodale).</summary>
public sealed class CaptureEcranTool : ITool
{
    public string Name => "capture_ecran";
    public string Description =>
        "Capture l'écran principal en PNG et retourne le chemin du fichier. Utile pour garder une trace visuelle du moment.";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters { get; } = Array.Empty<ToolParameter>();

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken cancellationToken = default)
    {
        var chemin = ScreenCaptureProbe.Capture();
        return Task.FromResult(chemin is null
            ? ToolResult.Failed("Capture d'écran impossible.")
            : ToolResult.Succeeded($"Écran capturé : {chemin}"));
    }
}
