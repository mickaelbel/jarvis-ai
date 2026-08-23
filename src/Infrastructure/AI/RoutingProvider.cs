using JarvisAI.Application.AI;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.AI;

/// <summary>
/// Aiguille les requêtes vers le bon fournisseur selon le modèle demandé.
/// Chaque fournisseur se déclare compatible avec des modèles (liste exacte ou
/// préfixes) ; le routage choisit le premier fournisseur disponible qui
/// reconnaît le modèle. Ollama reste la source par défaut en dernier recours.
/// </summary>
public sealed class RoutingProvider : IAIProvider
{
    private readonly IReadOnlyList<IAIProvider> _providers;
    private readonly ILogger<RoutingProvider> _logger;

    public string Name => "JarvisAI (multi-source)";

    public bool IsAvailable => _providers.Any(p => p.IsAvailable);

    public IReadOnlyList<string> KnownModels =>
        _providers.SelectMany(p => p.KnownModels).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public bool MatchesModel(string? model) =>
        _providers.Any(p => p is not RoutingProvider && p.MatchesModel(model));

    public RoutingProvider(IEnumerable<IAIProvider> providers, ILogger<RoutingProvider> logger)
    {
        _providers = providers.ToArray();
        _logger = logger;
    }

    public async Task<AIResponse> ChatAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var provider = Resolve(request.Model);
        _logger.LogDebug("[Routing] Chat routed to {Provider} for model '{Model}'", provider.Name, request.Model);
        return await provider.ChatAsync(request, cancellationToken);
    }

    public IAsyncEnumerable<AIStreamChunk> StreamChatAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var provider = Resolve(request.Model);
        _logger.LogDebug("[Routing] Stream routed to {Provider} for model '{Model}'", provider.Name, request.Model);
        return provider.StreamChatAsync(request, cancellationToken);
    }

    private IAIProvider Resolve(string? model)
    {
        var ollama = _providers.FirstOrDefault(p => p is OllamaProvider);
        var name = model?.Trim() ?? "";

        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (var provider in _providers)
            {
                if (provider is RoutingProvider) continue;
                if (provider is OllamaProvider) continue;
                if (provider.IsAvailable && provider.MatchesModel(name))
                {
                    _logger.LogInformation("[Routing] Model '{Model}' routed to {Provider}", name, provider.Name);
                    return provider;
                }
            }

            _logger.LogWarning("[Routing] Model '{Model}' not recognized by any cloud provider; falling back to Ollama", name);
        }

        if (ollama is not null && ollama.IsAvailable) return ollama;
        return _providers.FirstOrDefault(p => p.IsAvailable) ?? _providers.First();
    }
}
