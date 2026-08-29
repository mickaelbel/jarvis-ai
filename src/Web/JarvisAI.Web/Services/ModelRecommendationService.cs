using JarvisAI.Application.AI;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

public sealed record SmartRoute(
    string Model,
    ModelProfile Profile,
    string Reason,
    bool MultiStep,
    OllamaCatalogModel? DownloadSuggestion);

/// <summary>
/// Service de routage intelligent : combine le routeur heuristique (rapide) avec
/// le sélecteur IA (classification par petit modèle) pour choisir le meilleur
/// modèle installé pour la tâche, et proposer en téléchargement le meilleur
/// modèle de la catégorie s'il n'est pas installé.
/// </summary>
public sealed class ModelRecommendationService
{
    private readonly IModelRouter _router;
    private readonly IModelSelector _selector;
    private readonly SmartRoutingStore _settings;
    private readonly OllamaModelService _ollama;
    private readonly ILogger<ModelRecommendationService> _logger;

    private readonly object _cacheLock = new();
    private DateTime _installedCacheAt = DateTime.MinValue;
    private IReadOnlyList<string> _installedCache = Array.Empty<string>();

    private static readonly TimeSpan InstalledCacheTtl = TimeSpan.FromSeconds(10);

    public ModelRecommendationService(
        IModelRouter router,
        IModelSelector selector,
        SmartRoutingStore settings,
        OllamaModelService ollama,
        ILogger<ModelRecommendationService> logger)
    {
        _router = router;
        _selector = selector;
        _settings = settings;
        _ollama = ollama;
        _logger = logger;
    }

    public async Task<SmartRoute> ResolveAsync(
        string text,
        AIConversation? conversation,
        bool allowMultiStep,
        CancellationToken cancellationToken = default)
    {
        var route = _router.Resolve(text, conversation, ModelSelectionMode.Auto);

        var settings = _settings.Get();
        if (!settings.Enabled)
            return new SmartRoute(route.Model, route.Profile, route.Reason, false, null);

        var installed = await GetInstalledModelNamesAsync(cancellationToken);

        // Demande simple et courte : le routeur heuristique suffit, on évite un
        // aller-retour IA coûteux (réactivité vocale).
        if (route.Profile == ModelProfile.Fast && text.Length <= 100)
        {
            if (IsInstalled(route.Model, installed))
                return new SmartRoute(route.Model, route.Profile, route.Reason, false, null);

            // Le modèle rapide routé n'est pas installé : proposer le meilleur
            // petit modèle à télécharger pour que le chat puisse l'installer.
            var quickSpecs = KnownModels.Get(TaskCategory.Quick);
            var quickSuggestion = quickSpecs.Count > 0
                ? BuildCatalogSuggestion(quickSpecs[0].Name, installed)
                : null;
            return new SmartRoute(route.Model, route.Profile, route.Reason, false, quickSuggestion);
        }

        var recommendation = await _selector.ClassifyAsync(text, conversation, cancellationToken);
        if (recommendation is null)
            return new SmartRoute(route.Model, route.Profile, $"{route.Reason}; classification IA indisponible", false, null);

        var specs = KnownModels.Get(recommendation.Category);

        var chosen = specs.FirstOrDefault(s => IsInstalled(s.Name, installed))?.Name;
        var multiStep = allowMultiStep && settings.MultiStepEnabled && recommendation.MultiStep;

        var suggestion = BuildDownloadSuggestion(recommendation, installed, chosen);

        if (chosen is null)
        {
            return new SmartRoute(
                route.Model,
                route.Profile,
                $"{route.Reason}; smart:{recommendation.Category} ({recommendation.Reason})",
                multiStep,
                suggestion);
        }

        return new SmartRoute(
            chosen,
            ProfileFor(recommendation.Category),
            $"smart:{recommendation.Category} ({recommendation.Reason})",
            multiStep,
            suggestion);
    }

    private OllamaCatalogModel? BuildDownloadSuggestion(ModelRecommendation recommendation, IReadOnlyList<string> installed, string? chosen)
    {
        if (IsInstalled(recommendation.RecommendedModel, installed)) return null;

        var catalog = OllamaModelService.FindCatalogModel(recommendation.RecommendedModel);
        if (catalog is null) return null;

        // Ne pas proposer de télécharger le modèle déjà utilisé.
        if (!string.IsNullOrEmpty(chosen) && SameBase(catalog.Name, chosen)) return null;

        return catalog;
    }

    private static OllamaCatalogModel? BuildCatalogSuggestion(string modelName, IReadOnlyList<string> installed)
    {
        if (IsInstalled(modelName, installed)) return null;
        return OllamaModelService.FindCatalogModel(modelName);
    }

    private static ModelProfile ProfileFor(TaskCategory category)
        => category == TaskCategory.Quick ? ModelProfile.Fast : ModelProfile.Reasoning;

    private async Task<IReadOnlyList<string>> GetInstalledModelNamesAsync(CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (DateTime.UtcNow - _installedCacheAt < InstalledCacheTtl)
                return _installedCache;
        }

        var models = await _ollama.GetLocalModelsAsync(ct);
        var names = models.Select(m => m.Name).ToList();

        lock (_cacheLock)
        {
            _installedCacheAt = DateTime.UtcNow;
            _installedCache = names;
            return names;
        }
    }

    private static bool IsInstalled(string modelName, IReadOnlyList<string> installed)
    {
        var baseName = modelName.Trim().Split(':')[0];
        return installed.Any(m =>
            string.Equals(m, modelName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.Trim().Split(':')[0], baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameBase(string a, string b)
        => string.Equals(a.Trim().Split(':')[0], b.Trim().Split(':')[0], StringComparison.OrdinalIgnoreCase);
}
