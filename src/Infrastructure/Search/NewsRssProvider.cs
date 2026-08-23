using System.Text.RegularExpressions;
using System.Xml.Linq;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class NewsRssProvider : IWebSearchProvider
{
    private static readonly string[] DefaultFeeds =
    {
        "https://news.google.com/rss/search?q={query}&hl=fr&gl=FR&ceid=FR:fr",
        "https://www.lemonde.fr/rss/une.xml",
        "https://www.francetvinfo.fr/titres.rss",
        "https://feeds.bbci.co.uk/news/rss.xml",
        "https://www.theverge.com/rss/index.xml",
        "https://feeds.arstechnica.com/arstechnica/index",
        "https://techcrunch.com/feed/"
    };

    private readonly HttpClient _http;
    private readonly ILogger<NewsRssProvider> _logger;
    private readonly IReadOnlyList<string> _feeds;

    public NewsRssProvider(HttpClient http, ILogger<NewsRssProvider> logger, IEnumerable<string>? feeds = null)
    {
        _http = http;
        _logger = logger;
        _feeds = feeds?.ToList() ?? DefaultFeeds.ToList();
    }

    public string Name => "news";

    public bool CanHandle(SearchRequest request)
        => request.Type is SearchResultType.News or SearchResultType.General;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var tasks = _feeds.Select(feed => FetchFeedAsync(feed, request, cancellationToken)).ToArray();
        var all = await Task.WhenAll(tasks);

        var merged = all.SelectMany(x => x).ToList();
        var deduped = merged
            .GroupBy(r => SearchResultDeduper.CanonicalKey(r.Url) ?? r.Url, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .Take(request.MaxResults)
            .ToList();

        _logger.LogDebug("[News] {Query}: {Count} results", request.Query, deduped.Count);
        return deduped;
    }

    private async Task<IReadOnlyList<SearchResult>> FetchFeedAsync(string feed, SearchRequest request, CancellationToken ct)
    {
        try
        {
            var url = feed.Replace("{query}", Uri.EscapeDataString(request.Query));
            var xml = await _http.GetStringAsync(url, ct);
            return ParseFeed(xml, request);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[News] Feed {Feed} failed: {Error}", feed, ex.Message);
            return Array.Empty<SearchResult>();
        }
    }

    internal static IReadOnlyList<SearchResult> ParseFeed(string xml, SearchRequest request)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception)
        {
            return Array.Empty<SearchResult>();
        }

        var items = doc.Descendants("item").ToList();
        var results = new List<SearchResult>();
        var significant = SignificantTerms(request.Query);

        foreach (var item in items)
        {
            var title = item.Element("title")?.Value.Trim() ?? string.Empty;
            var link = item.Element("link")?.Value.Trim() ?? string.Empty;
            var description = item.Element("description")?.Value ?? string.Empty;
            var pubDate = item.Element("pubDate")?.Value ?? string.Empty;
            var source = item.Element("source")?.Value?.Trim();

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
                continue;

            if (significant.Count > 0)
            {
                var combined = (title + " " + description).ToLowerInvariant();
                if (!significant.Any(t => combined.Contains(t)))
                    continue;
            }

            var sourceName = source;
            if (string.IsNullOrWhiteSpace(sourceName) && title.Contains(" - ", StringComparison.Ordinal))
                sourceName = title[(title.LastIndexOf(" - ", StringComparison.Ordinal) + 3)..].Trim();

            var plainDescription = SearchHttp.StripHtml(System.Net.WebUtility.HtmlDecode(description));

            results.Add(new SearchResult
            {
                Title = title,
                Url = link,
                Snippet = plainDescription.Length > 250 ? plainDescription[..250] + "..." : plainDescription,
                Provider = "news",
                Source = sourceName ?? SearchResultDeduper.CanonicalDomain(link),
                Type = SearchResultType.News,
                PublishedAt = ParseDate(pubDate)
            });
        }

        return results;
    }

    private static DateTimeOffset? ParseDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var trimmed = raw.Trim();
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (DateTimeOffset.TryParseExact(trimmed, "ddd, dd MMM yyyy HH:mm:ss 'GMT'", invariant, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;
        if (DateTimeOffset.TryParse(trimmed, invariant, System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeUniversal, out var fallback))
            return fallback;
        return null;
    }

    private static List<string> SignificantTerms(string query)
    {
        var words = Regex.Split(query ?? string.Empty, @"\W+")
            .Where(w => w.Length >= 4 && !StopWords.Contains(w, StringComparer.OrdinalIgnoreCase))
            .Select(w => w.ToLowerInvariant())
            .ToList();
        return words;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "your", "you", "are",
        "not", "but", "has", "have", "was", "were", "all", "can", "will", "just",
        "news", "actualité", "actualite", "dernières", "dernieres", "derniere",
        "le", "la", "les", "des", "une", "un", "pour", "avec", "dans", "sur",
        "plus", "que", "qui", "est", "sont", "d'", "qu'", "après", "apres",
        "france", "monde", "monde", "info", "fr", "en", "au", "aux"
    };
}
