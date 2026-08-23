using JarvisAI.Application.Agents;
using JarvisAI.Application.Search;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class WebSearchTool : ITool
{
    private readonly IWebSearchService _search;
    private readonly ILogger<WebSearchTool> _logger;
    private readonly BrowserManager _browserManager;
    private readonly IWebPageContentService? _contentService;
    private readonly IEntityResolver _entityResolver;
    private readonly QueryInterpreter _interpreter = new();
    private readonly ConsensusAnalyzer _consensus = new();

    [ActivatorUtilitiesConstructor]
    public WebSearchTool(
        IWebSearchService search,
        ILogger<WebSearchTool> logger,
        BrowserManager browserManager,
        IWebPageContentService? contentService = null,
        IEntityResolver? entityResolver = null)
    {
        _search = search;
        _logger = logger;
        _browserManager = browserManager;
        _contentService = contentService;
        _entityResolver = entityResolver ?? new OfficialEntityResolver();
    }

    public WebSearchTool(
        IWebSearchService search,
        ILogger<WebSearchTool> logger,
        Action<string>? openUrl = null,
        IWebPageContentService? contentService = null)
        : this(search, logger,
               new BrowserManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserManager>.Instance,
                   processOpener: openUrl ?? ThrowIfOpenedInTest),
               contentService)
    {
        _testOpenUrl = openUrl;
    }

    private static void ThrowIfOpenedInTest(string url)
        => throw new InvalidOperationException(
            $"Ouverture de navigateur bloquée : WebSearchTool utilisé sans opener de test ({url}).");

    private readonly Action<string>? _testOpenUrl;

    public string Name => "web_search";
    public string Description => "Recherche web multi-moteurs. Actions: search, open, open_maps, resolve_official, resolve_entity, verify_link, extract";
    public string Category => "web";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "search | open | open_maps | resolve_official | resolve_entity | verify_link | extract", typeof(string), required: true),
        new ToolParameter("query", "Requête de recherche ou de navigation", typeof(string)),
        new ToolParameter("url", "URL à vérifier, ouvrir ou extraire", typeof(string)),
        new ToolParameter("lat", "Latitude du lieu (open_maps)", typeof(string)),
        new ToolParameter("lon", "Longitude du lieu (open_maps)", typeof(string)),
        new ToolParameter("max_results", "Nombre maximum de résultats", typeof(string)),
    };

    public async Task<ToolResult> ExecuteAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("query", out var query);
        parameters.TryGetValue("url", out var url);
        parameters.TryGetValue("lat", out var latStr);
        parameters.TryGetValue("lon", out var lonStr);
        int.TryParse(parameters.GetValueOrDefault("max_results"), out var maxResults);

        var effectiveAction = string.IsNullOrWhiteSpace(action) ? "search" : action;

        try
        {
            return effectiveAction.ToLowerInvariant() switch
            {
                "search" => await SearchAsync(query, maxResults, cancellationToken),
                "open"   => await OpenAsync(query, cancellationToken),
                "open_maps" => await OpenMapsAsync(query, latStr, lonStr, cancellationToken),
                "resolve_official" => await ResolveOfficialAsync(query, cancellationToken),
                "resolve_entity" => await ResolveEntityAsync(query, cancellationToken),
                "verify_link" => await VerifyLinkAsync(url, cancellationToken),
                "extract" => await ExtractAsync(url, cancellationToken),
                _ => ToolResult.Failed($"Action inconnue : '{action}'. Valides : search, open, open_maps, resolve_official, resolve_entity, verify_link, extract")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WebSearchTool] Action {Action} échouée", action);
            return ToolResult.Failed($"Erreur de recherche web : {ex.Message}");
        }
    }

    private async Task<ToolResult> SearchAsync(string? query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Failed("Paramètre 'query' requis.");

        var request = _interpreter.Interpret(query, maxResults > 0 ? maxResults : 10);
        var response = await _search.SearchAsync(request, ct);

        if (response.Results.Count == 0)
            return ToolResult.Succeeded("Aucun résultat trouvé.");

        var consensus = _consensus.Analyze(response.Results);
        var lines = new List<string>
        {
            $"Résultats pour « {response.Query} » (providers : {string.Join(", ", response.ProvidersUsed)})",
            $"Consensus : {consensus.Agreement:P0} · sources : {consensus.TotalSources} · domaines : {consensus.DistinctDomains}",
        };

        if (consensus.Contradictions.Count > 0)
        {
            lines.Add("⚠ Sources contradictoires :");
            lines.AddRange(consensus.Contradictions.Select(c => $"  - {c}"));
        }

        lines.Add("");
        lines.AddRange(response.Results.Select(CitationBuilder.Format));
        return ToolResult.Succeeded(string.Join("\n", lines));
    }

    private async Task<ToolResult> OpenAsync(string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Failed("Paramètre 'query' requis.");

        var resolver = new OpenActionResolver();
        var resolved = resolver.Resolve(query);

        string? url = null;
        string? choiceReason = null;

        if (resolved.DirectUrl is not null)
        {
            url = resolved.DirectUrl;
            choiceReason = $"entité officielle résolue ({resolved.Kind}, confiance {resolved.Confidence:P0})";
        }
        else
        {
            var entity = await _entityResolver.ResolveOfficialAsync(query, ct);
            if (entity is not null)
            {
                url = entity.Url;
                choiceReason = $"entité résolue ({entity.Kind}, confiance {entity.Confidence:P0})";
            }
        }

        if (url is null && resolved.SearchQuery is not null)
        {
            var req = _interpreter.Interpret(resolved.SearchQuery, 5);
            req = new SearchRequest
            {
                Query      = req.Query,
                Language   = req.Language,
                Type       = req.Type,
                MaxResults = 8,
                VerifyLinks = true
            };
            var resp = await _search.SearchAsync(req, ct);
            var best = BestResultPicker.Pick(resp.Results, req.Type, relevanceQuery: resolved.SearchQuery);
            if (best is not null)
            {
                url = best.Url;
                choiceReason = $"meilleur résultat classé (score {best.FinalScore:F1}, type {best.Type})";
            }
        }

        if (url is null)
            return ToolResult.Failed($"Impossible de déterminer une URL pertinente pour : {query}");

        return await OpenOnceAsync(url, choiceReason, ct);
    }

    private async Task<ToolResult> OpenOnceAsync(string url, string? reason, CancellationToken ct)
    {
        if (!await _search.VerifyLinkAsync(url, ct))
            return ToolResult.Failed($"Lien refusé après vérification : {url}");

        if (_testOpenUrl is not null)
        {
            _testOpenUrl(url);
            return ToolResult.Succeeded($"ACTION TERMINÉE — Ouvert dans le navigateur : {url} ({(string.IsNullOrWhiteSpace(reason) ? "résolu" : reason)})");
        }

        var browserResult = _browserManager.OpenUrl(url);
        if (browserResult.Success)
            return ToolResult.Succeeded($"ACTION TERMINÉE — {browserResult.Output}\nNe fais RIEN d'autre. Réponds à l'utilisateur maintenant.");
        return browserResult;
    }

    private async Task<ToolResult> OpenMapsAsync(string? query, string? latStr, string? lonStr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Failed("Paramètre 'query' requis.");

        var intent = MapsIntentParser.Parse(query);
        string url;
        string description;

        if (intent.Kind == MapsIntentKind.Directions && !string.IsNullOrWhiteSpace(intent.Destination))
        {
            var destination = Uri.EscapeDataString(intent.Destination);
            url = $"https://www.google.com/maps/dir/?api=1&destination={destination}";
            if (!string.IsNullOrWhiteSpace(intent.Origin))
                url += $"&origin={Uri.EscapeDataString(intent.Origin)}";
            description = $"Itinéraire vers « {intent.Destination} »{(string.IsNullOrWhiteSpace(intent.Origin) ? "" : $" depuis « {intent.Origin} »")}";
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(latStr) && !string.IsNullOrWhiteSpace(lonStr) &&
                double.TryParse(latStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat) &&
                double.TryParse(lonStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon))
            {
                url = $"https://www.google.com/maps/search/?api=1&query={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                description = $"Position sur la carte ({lat:F4}, {lon:F4})";
            }
            else
            {
                url = $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(query)}";
                description = $"Recherche sur la carte : {query}";
            }
        }

        var result = await OpenOnceAsync(url, "carte Google Maps", ct);
        if (!result.Success)
            return result;
        return ToolResult.Succeeded($"Carte Google Maps ({description}) : {result.Output}");
    }

    private async Task<ToolResult> ResolveEntityAsync(string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Failed("Paramètre 'query' requis.");

        var entity = await _entityResolver.ResolveOfficialAsync(query, ct);
        if (entity is null)
            return ToolResult.Succeeded("Aucune entité officielle reconnue.");

        return ToolResult.Succeeded(
            $"{entity.Entity} → {entity.Url} (confiance {entity.Confidence:P0}, {entity.Kind})");
    }

    private async Task<ToolResult> ResolveOfficialAsync(string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Failed("Paramètre 'query' requis.");

        var match = await _search.ResolveOfficialAsync(query, ct);
        return match is null
            ? ToolResult.Succeeded("Aucun site officiel reconnu.")
            : ToolResult.Succeeded($"{match.Name} → {match.Url} (confiance {match.Confidence:P0}, {match.MatchKind})");
    }

    private async Task<ToolResult> VerifyLinkAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Failed("Paramètre 'url' requis.");

        var ok = await _search.VerifyLinkAsync(url, ct);
        return ok
            ? ToolResult.Succeeded($"Lien vérifié (OK) : {url}")
            : ToolResult.Failed($"Lien refusé : {url}");
    }

    private async Task<ToolResult> ExtractAsync(string? url, CancellationToken ct)
    {
        if (_contentService is null)
            return ToolResult.Failed("Extraction de contenu non disponible.");
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Failed("Paramètre 'url' requis.");

        var extracted = await _contentService.ExtractAsync(url, ct);
        if (extracted is null)
            return ToolResult.Failed($"Impossible d'extraire le contenu de : {url}");

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(extracted.Title))
            lines.Add($"# {extracted.Title}");
        lines.Add(string.IsNullOrWhiteSpace(extracted.Text) ? "(aucun texte extrait)" : extracted.Text);
        if (extracted.Citations.Count > 0)
        {
            lines.Add("");
            lines.Add("Sources :");
            lines.AddRange(extracted.Citations.Select(c => $"  - {c}"));
        }
        return ToolResult.Succeeded(string.Join("\n", lines));
    }
}
