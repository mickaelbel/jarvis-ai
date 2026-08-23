using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Infrastructure.Tools.Vision;

/// <summary>
/// « Montre-moi ça » / « qu'est-ce que tu vois ? » : capture + modèle de
/// vision local, réponse ensuite parlée par Jarvis.
/// </summary>
public sealed class DecritEcranTool : ITool
{
    public string Name => "decrit_ecran";
    public string Description =>
        "Regarde l'écran et répond à une question sur ce qui y est affiché (modèle de vision local Ollama : llava, minicpm-v…). " +
        "Utilise-le quand l'utilisateur demande ce que tu vois, ou pour lire du contenu visuel complexe.";
    public string Category => "vision";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("question", "Ce qu'il faut regarder/répondre à propos de l'écran", typeof(string), required: false),
        new ToolParameter("modele", "Modèle de vision Ollama (défaut llava)", typeof(string), required: false)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken cancellationToken = default)
    {
        try
        {
            parameters ??= new Dictionary<string, string>();
            var question = parameters.TryGetValue("question", out var q) && q.Trim().Length > 0
                ? q.Trim()
                : "Décris brièvement ce qui est affiché à l'écran.";
            var modele = parameters.TryGetValue("modele", out var m) && m.Trim().Length > 0
                ? m.Trim()
                : "llava";

            var reponse = await VisionClient.AnalyserEcranAsync(question, modele, cancellationToken);
            return reponse.StartsWith("Erreur", StringComparison.Ordinal)
                ? ToolResult.Failed(reponse)
                : ToolResult.Succeeded(reponse);
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Vision impossible : {ex.Message}");
        }
    }
}
