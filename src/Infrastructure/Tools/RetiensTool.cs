using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Mémoire procédurale : « retiens que je préfère… » — consignes permanentes
/// réinjectées automatiquement dans chaque réponse via le rappel épisodique.
/// </summary>
public sealed class RetiensTool : ITool
{
    private readonly IMemoryService _memory;

    public RetiensTool(IMemoryService memory) => _memory = memory;

    public string Name => "retiens";
    public string Description =>
        "Mémorise une préférence ou consigne permanente de l'utilisateur (ex : « pour le métro toujours la ligne 9 », « je déteste les emoji »). " +
        "Actions : ajoute (texte), liste. Ces préférences sont appliquées automatiquement ensuite.";
    public string Category => "memory";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("action", "ajoute | liste", typeof(string), required: true),
        new ToolParameter("texte", "La préférence à retenir, formulée clairement et de façon générale (ajoute)", typeof(string), required: false)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken cancellationToken = default)
    {
        try
        {
            parameters ??= new Dictionary<string, string>();
            var action = parameters.TryGetValue("action", out var a) ? a.Trim().ToLowerInvariant() : "";

            if (action == "liste")
            {
                var prefs = await _memory.SearchAsync(
                    new MemoryQuery { Category = "preferences", Limit = 20 }, cancellationToken);
                if (prefs.Count == 0) return ToolResult.Succeeded("Aucune préférence mémorisée pour l'instant.");
                return ToolResult.Succeeded("Préférences retenues :\n" + string.Join("\n",
                    prefs.OrderByDescending(e => e.CreatedAt).Select((e, i) => $"{i + 1}. {e.Content}")));
            }

            if (action == "ajoute")
            {
                var texte = parameters.TryGetValue("texte", out var t) ? t.Trim() : "";
                if (texte.Length < 3)
                    return ToolResult.Failed("Précise la préférence à retenir (paramètre texte).");
                await _memory.SaveMemoryAsync(
                    $"preference.{DateTime.UtcNow:yyyyMMdd.HHmmss}",
                    texte,
                    MemoryType.Conversation,
                    "preferences",
                    importance: 0.75f,
                    tier: MemoryTier.LongTerm,
                    metadata: new Dictionary<string, string> { ["source"] = context.Source },
                    cancellationToken: cancellationToken);
                return ToolResult.Succeeded($"Retenu définitivement : « {texte} ».");
            }

            return ToolResult.Failed("Action inconnue. Utilise ajoute ou liste.");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Erreur mémoire procédurale : {ex.Message}");
        }
    }
}
