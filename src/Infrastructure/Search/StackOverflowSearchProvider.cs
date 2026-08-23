using System.Text.Json;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class StackOverflowSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<StackOverflowSearchProvider> _logger;

    public StackOverflowSearchProvider(HttpClient http, ILogger<StackOverflowSearchProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Name => "stackoverflow";

    public bool CanHandle(SearchRequest request)
        => request.Type is SearchResultType.General or SearchResultType.OfficialSite;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var encoded = Uri.EscapeDataString(request.Query);
        var url = $"https://api.stackexchange.com/2.3/search/advanced?site=stackoverflow&order=desc&sort=relevance&q={encoded}&pagesize={request.MaxResults}";

        var json = await _http.GetStringAsync(url, cancellationToken);
        var parsed = ParseResults(json, request.MaxResults);
        _logger.LogDebug("[StackOverflow] {Query}: {Count} results", request.Query, parsed.Count);
        return parsed;
    }

    internal static IReadOnlyList<SearchResult> ParseResults(string json, int maxResults)
    {
        var results = new List<SearchResult>();
        using var doc = SearchHttp.TryParseJson(json);
        if (doc is null)
            return results;

        if (!doc.RootElement.TryGetProperty("items", out var items))
            return results;

        foreach (var item in items.EnumerateArray())
        {
            if (results.Count >= maxResults)
                break;

            var title = item.TryGetProperty("title", out var t) ? SearchHttp.StripHtml(System.Net.WebUtility.HtmlDecode(t.GetString() ?? string.Empty)) : string.Empty;
            var link = item.TryGetProperty("link", out var l) ? l.GetString() ?? string.Empty : string.Empty;
            var score = item.TryGetProperty("score", out var sc) ? sc.GetInt32() : 0;
            var answered = item.TryGetProperty("is_answered", out var ia) && ia.GetBoolean();
            var created = item.TryGetProperty("creation_date", out var cd) ? cd.GetInt64() : 0;
            var tags = item.TryGetProperty("tags", out var tagsProp)
                ? string.Join(", ", tagsProp.EnumerateArray().Select(x => x.GetString()))
                : string.Empty;

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
                continue;

            var snippet = (answered ? "[Répondu] " : "[Sans réponse] ") + $"Score {score}";
            if (tags.Length > 0)
                snippet += $" · {tags}";

            results.Add(new SearchResult
            {
                Title = title,
                Url = link,
                Snippet = snippet,
                Provider = "stackoverflow",
                Source = "stackoverflow.com",
                Type = SearchResultType.General,
                PublishedAt = created > 0 ? DateTimeOffset.FromUnixTimeSeconds(created) : null,
                ExtraScore = Math.Min(1.0, score / 100.0)
            });
        }

        return results;
    }
}
