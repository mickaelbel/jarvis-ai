using JarvisAI.Application.Agents;
using JarvisAI.Domain.Security;

namespace JarvisAI.Application.Tools;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    string Category { get; }
    SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    /// <summary>
    /// Exposé au serveur MCP (clients externes type Claude Desktop / Hermes).
    /// Seuls les outils domotique/PC sûrs l'activent — jamais les outils sensibles.
    /// </summary>
    bool McpExpose => false;

    /// <summary>
    /// Phrase d'accusé de réception prononcée/affichée immédiatement pendant
    /// l'exécution d'un outil lent (ex: « Je regarde ton écran… »). Null = outil rapide.
    /// </summary>
    string? WaitingPhrase => null;

    /// <summary>
    /// Faux = l'outil est retiré de la liste donnée au modèle (prérequis
    /// manquants : modèle vision absent, API non configurée…) pour qu'il
    /// cesse de l'appeler en boucle.
    /// </summary>
    bool IsAvailable => true;

    IReadOnlyList<ToolParameter> Parameters { get; }
    Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default);
}
