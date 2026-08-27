using JarvisAI.Application.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

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

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        foreach (var p in _providers)
        {
            if (p is RoutingProvider) continue;
            try
            {
                if (await p.IsAvailableAsync(cancellationToken).ConfigureAwait(false)) return true;
            }
            catch
            {
                // provider indisponible, on essaie le suivant
            }
        }
        return false;
    }

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
        var provider = await ResolveAsync(request.Model, cancellationToken);
        _logger.LogDebug("[Routing] Chat routed to {Provider} for model '{Model}'", provider.Name, request.Model);
        return await provider.ChatAsync(request, cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamChunk> StreamChatAsync(AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var provider = await ResolveAsync(request.Model, cancellationToken);
        _logger.LogDebug("[Routing] Stream routed to {Provider} for model '{Model}'", provider.Name, request.Model);
        await foreach (var chunk in provider.StreamChatAsync(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    private async Task<IAIProvider> ResolveAsync(string? model, CancellationToken ct)
    {
        var ollama = _providers.FirstOrDefault(p => p is OllamaProvider);
        var name = model?.Trim() ?? "";

        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (var provider in _providers)
            {
                if (provider is RoutingProvider) continue;
                if (provider is OllamaProvider) continue;
                if (await provider.IsAvailableAsync(ct).ConfigureAwait(false) && provider.MatchesModel(name))
                {
                    _logger.LogInformation("[Routing] Model '{Model}' routed to {Provider}", name, provider.Name);
                    return provider;
                }
            }

            _logger.LogWarning("[Routing] Model '{Model}' not recognized by any cloud provider; falling back to Ollama", name);
        }

        if (ollama is not null && await ollama.IsAvailableAsync(ct).ConfigureAwait(false)) return ollama;
        foreach (var p in _providers)
        {
            if (p is RoutingProvider) continue;
            if (await p.IsAvailableAsync(ct).ConfigureAwait(false)) return p;
        }
        return _providers.FirstOrDefault(p => p is not RoutingProvider) ?? _providers.First();
    }
}
