using JarvisAI.Application.Agents;
using JarvisAI.Application.Search;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class NewsTool : ITool
{
    private readonly IWebSearchService _search;
    private readonly ILogger<NewsTool> _logger;

    public NewsTool(IWebSearchService search, ILogger<NewsTool> logger)
    {
        _search = search;
        _logger = logger;
    }

    public string Name => "news";
    public string Description =>
        "Fetches the latest news headlines (French sources by default) about a topic or the general news. " +
        "Use it to answer questions like 'what's in the news?' or 'latest on <topic>'.";
    public string Category => "web";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("query", "Topic of the news to fetch, or 'general' for headlines (French sources)", typeof(string), required: false, defaultValue: "general"),
        new ToolParameter("max_results", "Maximum number of headlines (default 8)", typeof(int), defaultValue: 8)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = parameters.TryGetValue("query", out var q) && !string.IsNullOrWhiteSpace(q) ? q.Trim() : "actualité";
            var maxResults = 8;
            if (parameters.TryGetValue("max_results", out var raw) && int.TryParse(raw, out var parsed) && parsed > 0)
            {
                maxResults = parsed;
            }

            var response = await _search.SearchAsync(new SearchRequest
            {
                Query = query,
                Type = SearchResultType.News,
                MaxResults = maxResults,
                Language = "fr",
                VerifyLinks = false,
                PreferOfficial = false
            }, cancellationToken);

            if (response.Results.Count == 0)
            {
                return ToolResult.Failed("Aucune actualité trouvée.");
            }

            var sb = new System.Text.StringBuilder();
            foreach (var r in response.Results.Take(maxResults))
            {
                var date = r.PublishedAt is { } when ? when.ToString("dd/MM HH:mm") : "date inconnue";
                sb.AppendLine($"- {r.Title} ({r.Source}) [{date}]");
                if (!string.IsNullOrWhiteSpace(r.Snippet)) sb.AppendLine("    " + r.Snippet);
                sb.AppendLine("    " + r.Url);
            }
            return ToolResult.Succeeded(sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NewsTool] Échec");
            return ToolResult.Failed("Erreur lors de la récupération des actualités : " + ex.Message);
        }
    }
}
