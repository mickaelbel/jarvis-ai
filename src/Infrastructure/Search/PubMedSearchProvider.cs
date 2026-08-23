using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class PubMedSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<PubMedSearchProvider> _logger;

    public PubMedSearchProvider(HttpClient http, ILogger<PubMedSearchProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Name => "pubmed";

    public bool CanHandle(SearchRequest request)
        => request.Type is SearchResultType.Academic;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var encoded = Uri.EscapeDataString(request.Query);
        var searchUrl = $"https://eutils.ncbi.nlm.nih.gov/entrez/eutils/esearch.fcgi?db=pubmed&term={encoded}&retmax={request.MaxResults}&retmode=json";
        var searchJson = await _http.GetStringAsync(searchUrl, cancellationToken);
        var ids = ParseEsearch(searchJson);
        if (ids.Count == 0)
            return Array.Empty<SearchResult>();

        var summaryUrl = $"https://eutils.ncbi.nlm.nih.gov/entrez/eutils/esummary.fcgi?db=pubmed&id={string.Join(",", ids)}&retmode=json";
        var summaryJson = await _http.GetStringAsync(summaryUrl, cancellationToken);
        var parsed = ParseEsummary(summaryJson, request.MaxResults);
        _logger.LogDebug("[PubMed] {Query}: {Count} results", request.Query, parsed.Count);
        return parsed;
    }

    internal static IReadOnlyList<string> ParseEsearch(string json)
    {
        var ids = new List<string>();
        using var doc = SearchHttp.TryParseJson(json);
        if (doc is null)
            return ids;

        if (doc.RootElement.TryGetProperty("esearchresult", out var esearch) &&
            esearch.TryGetProperty("idlist", out var idList) &&
            idList.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in idList.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                    ids.Add(id.GetString()!);
            }
        }
        return ids;
    }

    internal static IReadOnlyList<SearchResult> ParseEsummary(string json, int maxResults)
    {
        var results = new List<SearchResult>();
        using var doc = SearchHttp.TryParseJson(json);
        if (doc is null)
            return results;

        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return results;

        if (!result.TryGetProperty("uids", out var uids) || uids.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var uid in uids.EnumerateArray())
        {
            if (results.Count >= maxResults)
                break;

            var id = uid.GetString();
            if (string.IsNullOrWhiteSpace(id) || !result.TryGetProperty(id, out var item))
                continue;

            var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var journal = item.TryGetProperty("fulljournalname", out var j) ? j.GetString() : null;
            var pubDate = item.TryGetProperty("pubdate", out var pd) ? pd.GetString() : null;
            var authors = item.TryGetProperty("authors", out var au) && au.ValueKind == JsonValueKind.Array
                ? au.EnumerateArray()
                    .Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Take(3)
                    .ToList()
                : new List<string?>();

            var doi = FindArticleId(item, "doi");
            var pmc = FindArticleId(item, "pmc");
            var url = doi is not null
                ? $"https://doi.org/{doi}"
                : pmc is not null
                    ? $"https://www.ncbi.nlm.nih.gov/pmc/articles/{pmc}/"
                    : $"https://pubmed.ncbi.nlm.nih.gov/{id}/";

            var snippetParts = new List<string?>();
            if (journal is not null)
                snippetParts.Add(journal);
            if (pubDate is not null)
                snippetParts.Add(pubDate);
            if (authors.Count > 0)
                snippetParts.Add(string.Join(", ", authors));

            results.Add(new SearchResult
            {
                Title = title,
                Url = url,
                Snippet = string.Join(" · ", snippetParts.Where(p => !string.IsNullOrWhiteSpace(p))),
                Provider = "pubmed",
                Source = "pubmed.ncbi.nlm.nih.gov",
                Type = SearchResultType.Academic,
                Author = authors.Count > 0 ? string.Join(", ", authors) : null,
                PublishedAt = ParsePubDate(pubDate)
            });
        }

        return results;
    }

    private static string? FindArticleId(JsonElement item, string idType)
    {
        if (!item.TryGetProperty("articleids", out var articleIds) || articleIds.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var aid in articleIds.EnumerateArray())
        {
            if (aid.TryGetProperty("idtype", out var type) &&
                string.Equals(type.GetString(), idType, StringComparison.OrdinalIgnoreCase) &&
                aid.TryGetProperty("value", out var value) &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        }
        return null;
    }

    internal static DateTimeOffset? ParsePubDate(string? pubDate)
    {
        if (string.IsNullOrWhiteSpace(pubDate))
            return null;

        if (DateTimeOffset.TryParse(pubDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        var match = Regex.Match(pubDate.Trim(), @"^(\d{4})\s+([A-Za-z]{3,9})\s+(\d{1,2})$");
        if (match.Success && MonthAbbreviations.TryGetValue(match.Groups[2].Value.ToLowerInvariant(), out var month))
        {
            if (DateTimeOffset.TryParseExact(
                    $"{match.Groups[1].Value}-{month}-{match.Groups[3].Value}",
                    "yyyy-MM-d",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out parsed))
                return parsed;
        }

        if (int.TryParse(pubDate.Trim(), out var year) && year is >= 1900 and <= 2100)
            return new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);

        return null;
    }

    private static readonly Dictionary<string, string> MonthAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = "01", ["january"] = "01",
        ["feb"] = "02", ["february"] = "02",
        ["mar"] = "03", ["march"] = "03",
        ["apr"] = "04", ["april"] = "04",
        ["may"] = "05",
        ["jun"] = "06", ["june"] = "06",
        ["jul"] = "07", ["july"] = "07",
        ["aug"] = "08", ["august"] = "08",
        ["sep"] = "09", ["sept"] = "09", ["september"] = "09",
        ["oct"] = "10", ["october"] = "10",
        ["nov"] = "11", ["november"] = "11",
        ["dec"] = "12", ["december"] = "12"
    };
}
