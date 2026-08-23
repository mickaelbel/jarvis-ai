using System.Text.Json;
using System.Text.RegularExpressions;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class WikipediaSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<WikipediaSearchProvider> _logger;

    public WikipediaSearchProvider(HttpClient http, ILogger<WikipediaSearchProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Name => "wikipedia";

    public bool CanHandle(SearchRequest request)
        => request.Type is SearchResultType.General or SearchResultType.Academic or SearchResultType.OfficialSite;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var lang = (request.Language ?? "fr").ToLowerInvariant();
        var encoded = Uri.EscapeDataString(request.Query);
        var url = $"https://{lang}.wikipedia.org/w/api.php?action=query&list=search&srsearch={encoded}&format=json&srlimit={request.MaxResults}&origin=*";

        var json = await _http.GetStringAsync(url, cancellationToken);
        var parsed = ParseResults(json, request.MaxResults);
        _logger.LogDebug("[Wikipedia] {Query}: {Count} results", request.Query, parsed.Count);
        return parsed;
    }

    internal static IReadOnlyList<SearchResult> ParseResults(string json, int maxResults, string language = "fr")
    {
        var results = new List<SearchResult>();
        using var doc = SearchHttp.TryParseJson(json);
        if (doc is null)
            return results;

        if (!doc.RootElement.TryGetProperty("query", out var query))
            return results;
        if (!query.TryGetProperty("search", out var search))
            return results;

        foreach (var item in search.EnumerateArray())
        {
            if (results.Count >= maxResults)
                break;

            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            snippet = System.Net.WebUtility.HtmlDecode(Regex.Replace(snippet, "<[^>]+>", ""));

            var pageUrl = $"https://{language}.wikipedia.org/wiki/{title.Replace(' ', '_')}";

            results.Add(new SearchResult
            {
                Title = title,
                Url = pageUrl,
                Snippet = snippet,
                Provider = "wikipedia",
                Source = $"{language}.wikipedia.org",
                Type = SearchResultType.General
            });
        }

        return results;
    }
}
