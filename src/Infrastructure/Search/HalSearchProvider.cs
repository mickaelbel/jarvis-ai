using System.Text.Json;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class HalSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<HalSearchProvider> _logger;

    public HalSearchProvider(HttpClient http, ILogger<HalSearchProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Name => "hal";

    public bool CanHandle(SearchRequest request)
        => request.Type is SearchResultType.Academic;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var encoded = Uri.EscapeDataString(request.Query);
        var url = $"https://api.archives-ouvertes.fr/search/?q={encoded}&rows={request.MaxResults}&wt=json&fl=title_s,uri_s,authFullName_s,producedDate_s,abstract_s,docType_s";

        var json = await _http.GetStringAsync(url, cancellationToken);
        var parsed = ParseResults(json, request.MaxResults);
        _logger.LogDebug("[HAL] {Query}: {Count} results", request.Query, parsed.Count);
        return parsed;
    }

    internal static IReadOnlyList<SearchResult> ParseResults(string json, int maxResults)
    {
        var results = new List<SearchResult>();
        using var doc = SearchHttp.TryParseJson(json);
        if (doc is null)
            return results;

        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("docs", out var docs) ||
            docs.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in docs.EnumerateArray())
        {
            if (results.Count >= maxResults)
                break;

            var uri = item.TryGetProperty("uri_s", out var uriProp) && uriProp.ValueKind == JsonValueKind.String
                ? uriProp.GetString()
                : null;
            var title = item.TryGetProperty("title_s", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(title))
                continue;

            var url = uri.StartsWith("http://", StringComparison.Ordinal) ? "https://" + uri[7..] : uri;
            if (!url.StartsWith("https://hal.science", StringComparison.OrdinalIgnoreCase))
                url = url.Replace("hal.archives-ouvertes.fr", "hal.science", StringComparison.OrdinalIgnoreCase);

            var abstractParts = item.TryGetProperty("abstract_s", out var abs)
                ? abs.ValueKind switch
                {
                    JsonValueKind.String => new[] { abs.GetString() },
                    JsonValueKind.Array => abs.EnumerateArray().Select(x => x.GetString()).ToArray(),
                    _ => Array.Empty<string?>()
                }
                : Array.Empty<string?>();
            var abstractText = abstractParts.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

            var authors = item.TryGetProperty("authFullName_s", out var au) && au.ValueKind == JsonValueKind.Array
                ? au.EnumerateArray().Select(x => x.GetString()).Where(a => !string.IsNullOrWhiteSpace(a)).Take(3).ToList()
                : new List<string?>();
            var producedDate = item.TryGetProperty("producedDate_s", out var pd) && pd.ValueKind == JsonValueKind.String
                ? pd.GetString()
                : null;
            var docType = item.TryGetProperty("docType_s", out var dt) && dt.ValueKind == JsonValueKind.String ? dt.GetString() : null;

            var snippetParts = new List<string?>();
            if (!string.IsNullOrWhiteSpace(abstractText))
                snippetParts.Add(abstractText.Length > 300 ? abstractText[..300] + "..." : abstractText);
            if (!string.IsNullOrWhiteSpace(docType))
                snippetParts.Add(docType);

            results.Add(new SearchResult
            {
                Title = title,
                Url = url,
                Snippet = string.Join(" · ", snippetParts.Where(p => !string.IsNullOrWhiteSpace(p))),
                Provider = "hal",
                Source = "hal.science",
                Type = SearchResultType.Academic,
                Author = authors.Count > 0 ? string.Join(", ", authors) : null,
                PublishedAt = DateTimeOffset.TryParse(producedDate, out var publishedParsed) ? publishedParsed : null
            });
        }

        return results;
    }
}
