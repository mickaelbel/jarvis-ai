using System.Text.Json;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class SemanticScholarSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<SemanticScholarSearchProvider> _logger;

    public SemanticScholarSearchProvider(HttpClient http, ILogger<SemanticScholarSearchProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Name => "semantic_scholar";

    public bool CanHandle(SearchRequest request)
        => request.Type is SearchResultType.Academic;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var encoded = Uri.EscapeDataString(request.Query);
        var url = $"https://api.semanticscholar.org/graph/v1/paper/search?query={encoded}&limit={request.MaxResults}&fields=title,url,abstract,publicationDate,authors,venue,citationCount,openAccessPdf";

        var json = await _http.GetStringAsync(url, cancellationToken);
        var parsed = ParseResults(json, request.MaxResults);
        _logger.LogDebug("[SemanticScholar] {Query}: {Count} results", request.Query, parsed.Count);
        return parsed;
    }

    internal static IReadOnlyList<SearchResult> ParseResults(string json, int maxResults)
    {
        var results = new List<SearchResult>();
        using var doc = SearchHttp.TryParseJson(json);
        if (doc is null)
            return results;

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in data.EnumerateArray())
        {
            if (results.Count >= maxResults)
                break;

            var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var url = (item.TryGetProperty("openAccessPdf", out var pdf) &&
                       pdf.ValueKind == JsonValueKind.Object &&
                       pdf.TryGetProperty("url", out var pdfUrl) &&
                       !string.IsNullOrWhiteSpace(pdfUrl.GetString()))
                ? pdfUrl.GetString()!
                : (item.TryGetProperty("url", out var u) && !string.IsNullOrWhiteSpace(u.GetString())
                    ? u.GetString()!
                    : null);
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var abstractText = item.TryGetProperty("abstract", out var a) ? a.GetString() : null;
            var publicationDate = item.TryGetProperty("publicationDate", out var pd) && !string.IsNullOrWhiteSpace(pd.GetString())
                ? pd.GetString()!
                : null;
            var venue = item.TryGetProperty("venue", out var v) && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()! : null;
            var citations = item.TryGetProperty("citationCount", out var cc) ? cc.GetInt32() : 0;
            var authors = item.TryGetProperty("authors", out var au) && au.ValueKind == JsonValueKind.Array
                ? au.EnumerateArray()
                    .Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Take(3)
                    .ToList()
                : new List<string?>();

            var snippetParts = new List<string?>();
            if (!string.IsNullOrWhiteSpace(abstractText))
                snippetParts.Add(abstractText.Length > 300 ? abstractText[..300] + "..." : abstractText);
            if (venue is not null)
                snippetParts.Add(venue);
            snippetParts.Add($"{citations} citations");

            results.Add(new SearchResult
            {
                Title = title,
                Url = url,
                Snippet = string.Join(" · ", snippetParts.Where(p => !string.IsNullOrWhiteSpace(p))),
                Provider = "semantic_scholar",
                Source = "semanticscholar.org",
                Type = SearchResultType.Academic,
                Author = authors.Count > 0 ? string.Join(", ", authors) : null,
                PublishedAt = DateTimeOffset.TryParse(publicationDate, out var publishedParsed) ? publishedParsed : null,
                ExtraScore = Math.Min(1.0, citations / 2000.0)
            });
        }

        return results;
    }
}
