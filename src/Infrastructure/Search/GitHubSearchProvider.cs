using System.Text.Json;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class GitHubSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubSearchProvider> _logger;

    public GitHubSearchProvider(HttpClient http, ILogger<GitHubSearchProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Name => "github";

    public bool CanHandle(SearchRequest request)
        => request.Type is SearchResultType.Repository or SearchResultType.General;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var official = OfficialSiteCatalog.TryGetOfficialRepo(request.Query.Trim(), out var officialRepo)
            ? officialRepo
            : null;

        var encoded = Uri.EscapeDataString(request.Query);
        var url = official is not null
            ? $"https://api.github.com/search/repositories?q={Uri.EscapeDataString("repo:" + official)}"
            : $"https://api.github.com/search/repositories?q={encoded}&sort=stars&order=desc&per_page={request.MaxResults}";

        var json = await _http.GetStringAsync(url, cancellationToken);
        var parsed = ParseResults(json, request.MaxResults, official);
        _logger.LogDebug("[GitHub] {Query}: {Count} results", request.Query, parsed.Count);
        return parsed;
    }

    internal static IReadOnlyList<SearchResult> ParseResults(string json, int maxResults, string? officialRepo = null)
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

            if (item.TryGetProperty("fork", out var forkProp) && forkProp.GetBoolean())
                continue;

            var fullName = item.TryGetProperty("full_name", out var fn) ? fn.GetString() ?? string.Empty : string.Empty;
            var htmlUrl = item.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(htmlUrl))
                continue;

            var description = item.TryGetProperty("description", out var d) ? d.GetString() : null;
            var stars = item.TryGetProperty("stargazers_count", out var sc) ? sc.GetInt32() : 0;
            var owner = item.TryGetProperty("owner", out var ownerProp) && ownerProp.TryGetProperty("login", out var ol)
                ? ol.GetString()
                : null;
            var language = item.TryGetProperty("language", out var lg) ? lg.GetString() : null;
            var updated = item.TryGetProperty("updated_at", out var up) && DateTimeOffset.TryParse(up.GetString(), out var updatedParsed)
                ? (DateTimeOffset?)updatedParsed
                : null;

            var isOfficial = officialRepo is not null &&
                            string.Equals(fullName, officialRepo, StringComparison.OrdinalIgnoreCase);

            results.Add(new SearchResult
            {
                Title = fullName,
                Url = htmlUrl,
                Snippet = string.IsNullOrWhiteSpace(description) ? $"{stars} stars" : $"{description} ({stars} stars)",
                Provider = "github",
                Source = "github.com",
                Type = SearchResultType.Repository,
                Author = owner,
                PublishedAt = updated,
                IsOfficial = isOfficial,
                ExtraScore = isOfficial ? 1.0 : Math.Min(1.0, stars / 50000.0)
            });
        }

        return results;
    }
}
