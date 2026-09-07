using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class LocalSearchTool : ToolBase
{
    private readonly ILocalSearchService _service;
    private readonly ILogger<LocalSearchTool> _logger;

    public override string Name => "local_search";
    public override string Description => "Rechercher dans les fichiers locaux. Usage: local_search(action: \"search\", query: \"mot cle\", path: \"C:/documents\", limit: \"50\"). Actions : index, search, regex, clear.";
    public override string Category => "files";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "index, search, regex, clear (requis)", typeof(string), required: true),
        new ToolParameter("path", "Chemin du dossier à indexer/rechercher", typeof(string)),
        new ToolParameter("query", "Texte à rechercher (pour search)", typeof(string)),
        new ToolParameter("pattern", "Expression régulière (pour regex)", typeof(string)),
        new ToolParameter("limit", "Nombre max de résultats (défaut 50)", typeof(string))
    };

    public LocalSearchTool(ILocalSearchService service, ILogger<LocalSearchTool> logger)
        : base(logger)
    {
        _service = service;
        _logger = logger;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        var action = RequireParam(parameters, "action").ToLowerInvariant();
        parameters.TryGetValue("path", out var path);
        parameters.TryGetValue("query", out var query);
        parameters.TryGetValue("pattern", out var pattern);
        parameters.TryGetValue("limit", out var limitStr);

        var limit = 50;
        if (int.TryParse(limitStr, out var parsed) && parsed > 0)
            limit = parsed;

        return action switch
        {
            "index" => await IndexAsync(path, ct),
            "search" => await SearchAsync(query, path, limit, ct),
            "regex" => await RegexAsync(pattern, path, ct),
            "clear" => await ClearAsync(path, ct),
            _ => Fail($"Action inconnue : '{action}'. Actions valides : index, search, regex, clear")
        };
    }

    private async Task<ToolResult> IndexAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Fail("Le paramètre 'path' est requis pour indexer.");

        var result = await _service.BuildIndexAsync(path, ct);
        if (!result.Success)
            return Fail("Échec de l'indexation.");

        return Ok($"Indexation terminée : {result.FilesIndexed} fichiers indexés depuis {path}.");
    }

    private async Task<ToolResult> SearchAsync(string? query, string? path, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Fail("Le paramètre 'query' est requis.");

        if (string.IsNullOrWhiteSpace(path))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var results = await _service.SearchAsync(query, path, limit, ct);
        if (results.Count == 0)
            return Ok($"Aucun résultat pour « {query} ».");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{results.Count} résultat(s) pour « {query} » :");
        foreach (var r in results)
        {
            var context = r.MatchContext.Length > 120 ? r.MatchContext[..120] + "..." : r.MatchContext;
            sb.AppendLine($"  {r.FilePath}");
            sb.AppendLine($"    {context}");
        }
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> RegexAsync(string? pattern, string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return Fail("Le paramètre 'pattern' est requis.");
        if (string.IsNullOrWhiteSpace(path))
            return Fail("Le paramètre 'path' est requis.");

        var results = await _service.SearchWithRegexAsync(pattern, path, ct);
        if (results.Count == 0)
            return Ok($"Aucune correspondance regex dans : {path}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{results.Count} fichier(s) avec correspondances pour le motif « {pattern} » :");
        foreach (var r in results.Take(50))
        {
            var context = r.MatchContext.Length > 120 ? r.MatchContext[..120] + "..." : r.MatchContext;
            sb.AppendLine($"  {r.FilePath} — {r.MatchContext}");
        }
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> ClearAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Fail("Le paramètre 'path' est requis.");

        var result = await _service.ClearIndexAsync(path, ct);
        return Ok($"Index supprimé pour : {path}");
    }
}
