using System.Text.Json;
using System.Text.RegularExpressions;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class CrossRefSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<CrossRefSearchProvider> _logger;

    public CrossRefSearchProvider(HttpClient http, ILogger<CrossRefSearchProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Name => "crossref";

    public bool CanHandle(SearchRequest request)
        => request.Type is SearchResultType.Academic;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var encoded = Uri.EscapeDataString(request.Query);
        var url = $"https://api.crossref.org/works?query={encoded}&rows={request.MaxResults}&select=title,DOI,abstract,author,issued,container-title,is-referenced-by-count";

        var json = await _http.GetStringAsync(url, cancellationToken);
        var parsed = ParseResults(json, request.MaxResults);
        _logger.LogDebug("[CrossRef] {Query}: {Count} results", request.Query, parsed.Count);
        return parsed;
    }

    internal static IReadOnlyList<SearchResult> ParseResults(string json, int maxResults)
    {
        var results = new List<SearchResult>();
        using var doc = SearchHttp.TryParseJson(json);
        if (doc is null)
            return results;

        if (!doc.RootElement.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in items.EnumerateArray())
        {
            if (results.Count >= maxResults)
                break;

            var title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0
                ? t[0].GetString()
                : null;
            var doi = item.TryGetProperty("DOI", out var d) ? d.GetString() : null;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(doi))
                continue;

            var abstractXml = item.TryGetProperty("abstract", out var a) ? a.GetString() : null;
            var abstractText = string.IsNullOrWhiteSpace(abstractXml)
                ? null
                : Regex.Replace(abstractXml, "<[^>]+>", " ").Trim();
            if (abstractText is not null)
                abstractText = Regex.Replace(abstractText, @"\s+", " ").Trim();

            var journal = item.TryGetProperty("container-title", out var ct) && ct.ValueKind == JsonValueKind.Array && ct.GetArrayLength() > 0
                ? ct[0].GetString()
                : null;
            var citations = item.TryGetProperty("is-referenced-by-count", out var cc) ? cc.GetInt32() : 0;
            var authors = item.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Array
                ? au.EnumerateArray()
                    .Select(x =>
                    {
                        var family = x.TryGetProperty("family", out var f) ? f.GetString() : null;
                        var given = x.TryGetProperty("given", out var g) ? g.GetString() : null;
                        return string.Join(" ", new[] { given, family }.Where(p => !string.IsNullOrWhiteSpace(p)));
                    })
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Take(3)
                    .ToList()
                : new List<string>();

            var issued = item.TryGetProperty("issued", out var isProp) &&
                         isProp.TryGetProperty("date-parts", out var dateParts) &&
                         dateParts.ValueKind == JsonValueKind.Array &&
                         dateParts.GetArrayLength() > 0 &&
                         dateParts[0].ValueKind == JsonValueKind.Array &&
                         dateParts[0].GetArrayLength() > 0
                ? dateParts[0][0].GetInt32()
                : (int?)null;

            var snippetParts = new List<string?>();
            if (!string.IsNullOrWhiteSpace(abstractText))
                snippetParts.Add(abstractText.Length > 300 ? abstractText[..300] + "..." : abstractText);
            if (journal is not null)
                snippetParts.Add(journal);
            snippetParts.Add($"{citations} citations");

            results.Add(new SearchResult
            {
                Title = title,
                Url = $"https://doi.org/{doi}",
                Snippet = string.Join(" · ", snippetParts.Where(p => !string.IsNullOrWhiteSpace(p))),
                Provider = "crossref",
                Source = "doi.org",
                Type = SearchResultType.Academic,
                Author = authors.Count > 0 ? string.Join(", ", authors) : null,
                PublishedAt = issued is { } year ? new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero) : null,
                ExtraScore = Math.Min(1.0, citations / 2000.0)
            });
        }

        return results;
    }
}
