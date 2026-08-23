using System.Xml.Linq;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class ArxivSearchProvider : IWebSearchProvider
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    private readonly HttpClient _http;
    private readonly ILogger<ArxivSearchProvider> _logger;

    public ArxivSearchProvider(HttpClient http, ILogger<ArxivSearchProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Name => "arxiv";

    public bool CanHandle(SearchRequest request)
        => request.Type is SearchResultType.Academic;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var encoded = Uri.EscapeDataString(request.Query);
        var url = $"http://export.arxiv.org/api/query?search_query=all:{encoded}&max_results={request.MaxResults}";

        var xml = await _http.GetStringAsync(url, cancellationToken);
        var parsed = ParseResults(xml, request.MaxResults);
        _logger.LogDebug("[Arxiv] {Query}: {Count} results", request.Query, parsed.Count);
        return parsed;
    }

    internal static IReadOnlyList<SearchResult> ParseResults(string xml, int maxResults)
    {
        var results = new List<SearchResult>();
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception)
        {
            return results;
        }

        foreach (var entry in doc.Descendants(Atom + "entry"))
        {
            if (results.Count >= maxResults)
                break;

            var title = entry.Element(Atom + "title")?.Value.Trim() ?? string.Empty;
            var id = entry.Element(Atom + "id")?.Value.Trim() ?? string.Empty;
            var summary = entry.Element(Atom + "summary")?.Value.Trim() ?? string.Empty;
            var published = entry.Element(Atom + "published")?.Value ?? string.Empty;
            var authors = entry.Elements(Atom + "author")
                .Select(a => a.Element(Atom + "name")?.Value.Trim())
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Take(3)
                .ToList();

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(id))
                continue;

            var url = id.StartsWith("http://", StringComparison.Ordinal)
                ? "https://" + id[7..]
                : id;

            results.Add(new SearchResult
            {
                Title = title,
                Url = url,
                Snippet = summary.Length > 300 ? summary[..300] + "..." : summary,
                Provider = "arxiv",
                Source = "arxiv.org",
                Type = SearchResultType.Academic,
                Author = authors.Count > 0 ? string.Join(", ", authors) : null,
                PublishedAt = DateTimeOffset.TryParse(published, out var publishedParsed) ? publishedParsed : null
            });
        }

        return results;
    }
}
