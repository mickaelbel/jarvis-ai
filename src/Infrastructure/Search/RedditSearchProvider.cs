using System.Text.Json;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class RedditSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<RedditSearchProvider> _logger;

    public RedditSearchProvider(HttpClient http, ILogger<RedditSearchProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Name => "reddit";

    public bool CanHandle(SearchRequest request)
        => request.Query.Contains("reddit", StringComparison.OrdinalIgnoreCase) ||
           request.Query.Contains("subreddit", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var encoded = Uri.EscapeDataString(request.Query);
        var url = $"https://www.reddit.com/search.json?q={encoded}&limit={request.MaxResults}&sort=relevance&t=all";

        var json = await _http.GetStringAsync(url, cancellationToken);
        var parsed = ParseResults(json, request.MaxResults);
        _logger.LogDebug("[Reddit] {Query}: {Count} results", request.Query, parsed.Count);
        return parsed;
    }

    internal static IReadOnlyList<SearchResult> ParseResults(string json, int maxResults)
    {
        var results = new List<SearchResult>();
        using var doc = SearchHttp.TryParseJson(json);
        if (doc is null)
            return results;

        if (!doc.RootElement.TryGetProperty("data", out var data))
            return results;
        if (!data.TryGetProperty("children", out var children))
            return results;

        foreach (var child in children.EnumerateArray())
        {
            if (results.Count >= maxResults)
                break;

            if (!child.TryGetProperty("data", out var d))
                continue;

            var title = d.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var permalink = d.TryGetProperty("permalink", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            var subreddit = d.TryGetProperty("subreddit_name_prefixed", out var sub) ? sub.GetString() : null;
            var score = d.TryGetProperty("score", out var sc) ? sc.GetInt32() : 0;
            var comments = d.TryGetProperty("num_comments", out var nc) ? nc.GetInt32() : 0;
            var created = d.TryGetProperty("created_utc", out var cu) ? cu.GetDouble() : 0;
            var selftext = d.TryGetProperty("selftext", out var st) ? st.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(permalink))
                continue;

            var url = $"https://www.reddit.com{permalink}";
            var snippet = string.IsNullOrWhiteSpace(selftext)
                ? $"{subreddit ?? "reddit"} · {score} votes · {comments} commentaires"
                : (selftext.Length > 200 ? selftext[..200] + "..." : selftext);

            results.Add(new SearchResult
            {
                Title = title,
                Url = url,
                Snippet = snippet,
                Provider = "reddit",
                Source = subreddit ?? "reddit.com",
                Author = subreddit,
                PublishedAt = created > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)created) : null,
                ExtraScore = Math.Min(1.0, score / 1000.0)
            });
        }

        return results;
    }
}
