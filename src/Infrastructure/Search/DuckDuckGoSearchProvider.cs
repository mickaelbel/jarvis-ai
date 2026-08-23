using System.Text.RegularExpressions;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class DuckDuckGoSearchProvider : IWebSearchProvider
{
    private static readonly Regex AnchorRegex = new(
        @"<a[^>]*class=""[^""]*result__a[^""]*""[^>]*href=""([^""]*)""[^>]*>(.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex SnippetRegex = new(
        @"<a[^>]*class=""[^""]*result__snippet[^""]*""[^>]*>(.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly ILogger<DuckDuckGoSearchProvider> _logger;

    public DuckDuckGoSearchProvider(HttpClient http, ILogger<DuckDuckGoSearchProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Name => "duckduckgo";

    public bool CanHandle(SearchRequest request)
        => request.Type is SearchResultType.General or SearchResultType.OfficialSite;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var encoded = Uri.EscapeDataString(request.Query);
        var region = (request.Language ?? "fr").ToLowerInvariant() == "fr" ? "fr-FR" : "us-en";
        var url = $"https://html.duckduckgo.com/html/?q={encoded}&kl={region}";

        try
        {
            var html = await _http.GetStringAsync(url, cancellationToken);
            var parsed = ParseResults(html, request.MaxResults);
            _logger.LogDebug("[DuckDuckGo] {Query}: {Count} results", request.Query, parsed.Count);
            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[DuckDuckGo] Query {Query} failed: {Error}", request.Query, ex.Message);
            return Array.Empty<SearchResult>();
        }
    }

    internal static IReadOnlyList<SearchResult> ParseResults(string html, int maxResults)    {
        var results = new List<SearchResult>();

        var anchors = AnchorRegex.Matches(html);
        var snippets = SnippetRegex.Matches(html);

        var count = Math.Min(anchors.Count, snippets.Count);
        for (var i = 0; i < count && results.Count < maxResults; i++)
        {
            var rawUrl = System.Net.WebUtility.HtmlDecode(anchors[i].Groups[1].Value);
            var url = SearchHttp.DecodeRedirectUrl(rawUrl);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                continue;
            if (uri.Scheme is not ("http" or "https"))
                continue;

            var title = SearchHttp.StripHtml(System.Net.WebUtility.HtmlDecode(anchors[i].Groups[2].Value));
            var snippet = SearchHttp.StripHtml(System.Net.WebUtility.HtmlDecode(snippets[i].Groups[1].Value));

            results.Add(new SearchResult
            {
                Title = title,
                Url = url,
                Snippet = snippet,
                Provider = "duckduckgo",
                Source = uri.Host
            });
        }

        return results;
    }
}
