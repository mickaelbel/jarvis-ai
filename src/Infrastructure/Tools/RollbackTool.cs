using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Dev;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

// Retour arrière conversationnel : annule les derniers changements de code de
// Jarvis (rewind git par tour), liste l'historique ou restaure sélectivement.
// Déclenché par la voix (« annule tes derniers changements ») ou le chat.
public sealed class RollbackTool : ITool
{
    private readonly ITurnHistory _history;
    private readonly ILogger<RollbackTool> _logger;

    public RollbackTool(ITurnHistory history, ILogger<RollbackTool> logger)
    {
        _history = history;
        _logger = logger;
    }

    public string Name => "retour";
    public string Description =>
        "Retour arrière sur tes propres modifications de code, tour par tour (historique git). " +
        "Actions : annule (nombre = combien de tours en arrière, défaut 1), historique (liste des derniers tours), " +
        "restaure (nombre + sauf = chemins à CONSERVER séparés par des virgules, ex : sauf=src/Infrastructure/AI,Tests/MonTest.cs).";
    public string Category => "dev";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;
    public bool McpExpose => false;
    public string WaitingPhrase => "Je reviens en arrière…";

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "annule | historique | restaure", typeof(string), required: true),
        new("nombre", "Nombre de tours en arrière (défaut 1)", typeof(string)),
        new("sauf", "Chemins à conserver lors d'une restauration (virgules)", typeof(string)),
        new("tour", "Id (même partiel) du tour cible pour annule", typeof(string))
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        var count = int.TryParse(parameters.GetValueOrDefault("nombre"), out var n) ? n : 1;
        try
        {
            return (action?.ToLowerInvariant().Trim()) switch
            {
                "annule" or "undo" or "revenir" when !string.IsNullOrWhiteSpace(parameters.GetValueOrDefault("tour")) =>
                    ToolResult.Succeeded(await _history.RewindBeforeAsync(parameters.GetValueOrDefault("tour")!.Trim())),
                "annule" or "undo" or "revenir" =>
                    ToolResult.Succeeded(await _history.UndoLastAsync(count)),
                "historique" or "liste" => ToolResult.Succeeded(FormatHistory()),
                "restaure" => ToolResult.Succeeded(await _history.RestoreSelectiveAsync(
                    count,
                    parameters.GetValueOrDefault("sauf")?
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))),
                _ => ToolResult.Failed($"Action inconnue : {action}. Valides : annule, historique, restaure.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Retour] action {Action} échouée", action);
            return ToolResult.Failed($"Erreur retour arrière : {ex.Message}");
        }
    }

    private string FormatHistory()
    {
        var turns = _history.List(15);
        if (turns.Count == 0)
            return "Aucun tour enregistré pour l'instant (l'historique démarre avec cette session).";
        var sb = new System.Text.StringBuilder($"DERNIERS TOURS ({turns.Count}, plus récent en premier) :\n");
        foreach (var t in turns.Reverse())
        {
            var local = t.AtUtc.ToLocalTime();
            var ask = t.UserText.ReplaceLineEndings(" ");
            if (ask.Length > 70) ask = ask[..70] + "…";
            sb.Append($"• [{t.Id}] {local:dd/MM HH:mm} ({t.Source}) « {ask} »");
            if (t.ChangedFiles.Count > 0)
                sb.Append($" — {t.ChangedFiles.Count} fichier(s) : {string.Join(", ", t.ChangedFiles.Take(3))}");
            else
                sb.Append(" — aucun fichier modifié");
            sb.AppendLine();
        }
        sb.Append("Pour annuler jusqu'à un tour précis : action=annule avec nombre ou tour=[id].");
        return sb.ToString();
    }
}
