using JarvisAI.Application.Security;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Application.AI;

/// <summary>
/// Sélecteur de modèles intelligent : classe la tâche utilisateur avec le petit modèle
/// rapide (JSON strict) puis recommande le meilleur modèle connu pour cette catégorie.
/// En cas d'échec/timeout, renvoie null (l'appelant retombe sur le routeur heuristique).
/// </summary>
public sealed class ModelSelector : IModelSelector
{
    private readonly Lazy<IAIService> _ai;
    private readonly ModelRouterOptions _options;
    private readonly ILogger<ModelSelector> _logger;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public ModelSelector(Lazy<IAIService> ai, ModelRouterOptions options, ILogger<ModelSelector> logger)
    {
        _ai = ai;
        _options = options;
        _logger = logger;
    }

    public async Task<ModelRecommendation?> ClassifyAsync(string text, AIConversation? conversation = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            var model = string.IsNullOrEmpty(_options.FastModel) ? KnownModels.BaseFastModel : _options.FastModel;
            var classifierConversation = new AIConversation(BuildClassifierPrompt());
            var response = await _ai.Value.ChatAsync(text, classifierConversation, model, ModelSelectionMode.Fast, timeout.Token);
            if (!response.Success || string.IsNullOrWhiteSpace(response.Content)) return null;

            var parsed = Parse(response.Content);
            if (parsed is null) return null;

            var specs = KnownModels.Get(parsed.Category);
            var best = specs[0];
            return new ModelRecommendation(
                parsed.Category,
                best.Name,
                best.Parameters,
                best.DownloadSize,
                parsed.MultiStep,
                parsed.Reason ?? "classification IA");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[ModelSelector] Classification timed out");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ModelSelector] Classification failed");
            return null;
        }
    }

    private static string BuildClassifierPrompt()
        => """
           Tu es un routeur de modèles IA. Ta seule mission : classer la demande utilisateur dans UNE de ces catégories :
           - quick : demande triviale, réponse courte suffit (salutation, remerciement, heure, oui/non).
           - general : conversation générale, explication, aide courante.
           - code : écrire, corriger, refactorer, déboguer ou analyser du code, un script, une requête SQL, une config.
           - math : calculs, équations, démonstrations, algèbre, statistiques.
           - reasoning : analyse poussée, logique, problème complexe, comparaison approfondie, dilemme.
           - planning : organiser, planifier des étapes, stratégie, feuille de route, préparation d'un travail complexe.
           - research : synthèse de plusieurs sources, rapport, recherche documentaire, résumé long.
           - creative : écriture, poésie, idées, marketing, traduction littéraire, story.
           Réponds UNIQUEMENT en JSON valide sur une seule ligne, format exact :
           {"category":"code","multi_step":true,"reason":"justification très courte"}
           multi_step : true seulement si la tâche justifie d'abord un plan puis une exécution (projet de code, architecture, tâche complexe multi-étapes, recherche approfondie). Sinon false.
           """;

    private sealed record Parsed(TaskCategory Category, bool MultiStep, string? Reason);

    private static Parsed? Parse(string content)
    {
        var json = ExtractJson(content);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("category", out var cat)) return null;
            var category = MapCategory(cat.GetString());
            var multiStep = root.TryGetProperty("multi_step", out var ms) && ms.ValueKind == JsonValueKind.True;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
            return new Parsed(category, multiStep, reason);
        }
        catch
        {
            return null;
        }
    }

    private static TaskCategory MapCategory(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "quick" => TaskCategory.Quick,
        "code" => TaskCategory.Code,
        "math" => TaskCategory.Math,
        "reasoning" => TaskCategory.Reasoning,
        "planning" => TaskCategory.Planning,
        "research" => TaskCategory.Research,
        "creative" => TaskCategory.Creative,
        _ => TaskCategory.General
    };

    private static string? ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return content[start..(end + 1)];
    }
}
