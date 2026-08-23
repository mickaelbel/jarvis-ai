using System.Text.RegularExpressions;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class BingSearchProvider : IWebSearchProvider
{
    private static readonly Regex AlgoRegex = new(
        @"<li class=""b_algo"">(?<block>.*?)</li>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex TitleRegex = new(
        @"<h2[^>]*>\s*<a[^>]*href=""(?<url>[^""]+)""[^>]*>(?<title>.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex SnippetRegex = new(
        @"<p[^>]*class=""[^""]*b_lineclamp[^""]*""[^>]*>(?<snippet>.*?)</p>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly ILogger<BingSearchProvider> _logger;

    public BingSearchProvider(HttpClient http, ILogger<BingSearchProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Name => "bing";

    public bool CanHandle(SearchRequest request)
        => request.Type is SearchResultType.General or SearchResultType.OfficialSite;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var encoded = Uri.EscapeDataString(request.Query);
        var lang = (request.Language ?? "fr").ToLowerInvariant();
        var region = lang == "fr" ? "fr-FR" : "en-US";
        var url = $"https://www.bing.com/search?q={encoded}&setlang={lang}&mkt={region}";

        var html = await _http.GetStringAsync(url, cancellationToken);
        var parsed = ParseResults(html, request.MaxResults);
        _logger.LogDebug("[Bing] {Query}: {Count} results", request.Query, parsed.Count);
        return parsed;
    }

    internal static IReadOnlyList<SearchResult> ParseResults(string html, int maxResults)
    {
        var results = new List<SearchResult>();

        foreach (Match block in AlgoRegex.Matches(html))
        {
            if (results.Count >= maxResults)
                break;

            var blockHtml = block.Groups["block"].Value;
            var titleMatch = TitleRegex.Match(blockHtml);
            if (!titleMatch.Success)
                continue;

            var rawUrl = System.Net.WebUtility.HtmlDecode(titleMatch.Groups["url"].Value);
            var url = SearchHttp.DecodeRedirectUrl(rawUrl);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                continue;
            if (uri.Scheme is not ("http" or "https"))
                continue;

            var title = SearchHttp.StripHtml(titleMatch.Groups["title"].Value);
            var snippetMatch = SnippetRegex.Match(blockHtml);
            var snippet = snippetMatch.Success ? SearchHttp.StripHtml(snippetMatch.Groups["snippet"].Value) : string.Empty;

            results.Add(new SearchResult
            {
                Title = title,
                Url = url,
                Snippet = snippet,
                Provider = "bing",
                Source = uri.Host
            });
        }

        return results;
    }
}
