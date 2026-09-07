using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace JarvisAI.Infrastructure.Automation;

public interface IVeilleTechnoService
{
    Task<VeilleResult> CollectAsync(IEnumerable<string> sources, CancellationToken ct = default);
    Task<List<VeilleItem>> GetTrendingAsync(string source = "github", int limit = 20, CancellationToken ct = default);
    Task<string> GenerateReportAsync(IEnumerable<VeilleItem> items, CancellationToken ct = default);
}

public sealed class VeilleTechnoService : IVeilleTechnoService
{
    private readonly ILogger<VeilleTechnoService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _storagePath;

    public static readonly string[] DefaultSources = new[]
    {
        "github", "hackernews", "reddit", "devto", "stackoverflow",
        "arxiv", "lobsters", "slashdot", "thehackernews", "medium"
    };

    public VeilleTechnoService(ILogger<VeilleTechnoService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "JarvisAI-Veille/1.0");
        _storagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI", "veille_history.json");
    }

    public async Task<VeilleResult> CollectAsync(IEnumerable<string> sources, CancellationToken ct = default)
    {
        var result = new VeilleResult { CollectedAt = DateTime.UtcNow };
        var sourceList = (sources is not null && sources.Any())
            ? sources.ToList()
            : DefaultSources.ToList();

        foreach (var source in sourceList)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var items = await GetSourceItemsAsync(source, ct);
                result.Items.AddRange(items);
                result.SourcesCollected++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Veille] Failed: {Source}", source);
                result.Errors.Add(source);
            }
        }

        result.Success = true;
        SaveHistory(result.Items);
        _logger.LogInformation("[Veille] Collected {Count} items from {Sources} sources", result.Items.Count, result.SourcesCollected);
        return result;
    }

    public async Task<List<VeilleItem>> GetTrendingAsync(string source = "github", int limit = 20, CancellationToken ct = default)
    {
        return source.ToLowerInvariant() switch
        {
            "github" => await GetGithubTrendingAsync(limit, ct),
            "hackernews" or "hn" => await GetHackerNewsAsync(limit, ct),
            "reddit" => await GetRedditTrendingAsync(limit, ct),
            "devto" or "dev.to" => await GetDevToTrendingAsync(limit, ct),
            "stackoverflow" => await GetStackOverflowTrendingAsync(limit, ct),
            "arxiv" => await ParseRssAsync("http://export.arxiv.org/api/query?search_query=cat:cs.AI&sortBy=submittedDate&max_results=10", "arXiv", limit, ct),
            "lobsters" => await ParseRssAsync("https://lobste.rs/newest.rss", "Lobsters", limit, ct),
            "slashdot" => await ParseRssAsync("https://rss.slashdot.org/Slashdot/slashdotMain", "Slashdot", limit, ct),
            "thehackernews" or "thn" => await ParseRssAsync("https://feeds.feedburner.com/TheHackerNews", "TheHackerNews", limit, ct),
            "medium" => await ParseRssAsync("https://medium.com/feed/tag/artificial-intelligence", "Medium", limit, ct),
            _ => new List<VeilleItem>()
        };
    }

    public Task<string> GenerateReportAsync(IEnumerable<VeilleItem> items, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Rapport Veille Techno");
        sb.AppendLine($"Date: {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Items: {items.Count()}");
        sb.AppendLine();

        foreach (var item in items.GroupBy(i => i.Source).OrderBy(g => g.Key))
        {
            sb.AppendLine($"## {item.Key}");
            foreach (var i in item.Take(10))
            {
                sb.AppendLine($"- [{i.Title}]({i.Url})");
                if (!string.IsNullOrEmpty(i.Description))
                    sb.AppendLine($"  {i.Description[..Math.Min(100, i.Description.Length)]}...");
            }
            sb.AppendLine();
        }

        return Task.FromResult(sb.ToString());
    }

    private async Task<List<VeilleItem>> GetGithubTrendingAsync(int limit, CancellationToken ct)
    {
        var items = new List<VeilleItem>();
        try
        {
            var html = await _httpClient.GetStringAsync("https://github.com/trending", ct);
            var matches = System.Text.RegularExpressions.Regex.Matches(html, @"<a href=""(/[^""]+)""[^>]*>([^<]+)</a>.*?<p[^>]*>([^<]+)</p>");

            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (items.Count >= limit) break;
                items.Add(new VeilleItem
                {
                    Source = "GitHub",
                    Title = m.Groups[2].Value.Trim(),
                    Url = $"https://github.com{m.Groups[1].Value}",
                    Description = m.Groups[3].Value.Trim()
                });
            }
        }
        catch { }
        return items;
    }

    private async Task<List<VeilleItem>> GetHackerNewsAsync(int limit, CancellationToken ct)
    {
        var items = new List<VeilleItem>();
        try
        {
            var json = await _httpClient.GetStringAsync("https://hacker-news.firebaseio.com/v0/topstories.json", ct);
            var ids = JsonSerializer.Deserialize<List<int>>(json);

            if (ids is not null)
            {
                foreach (var id in ids.Take(limit))
                {
                    var storyJson = await _httpClient.GetStringAsync($"https://hacker-news.firebaseio.com/v0/item/{id}.json", ct);
                    var story = JsonSerializer.Deserialize<JsonElement>(storyJson);

                    items.Add(new VeilleItem
                    {
                        Source = "HackerNews",
                        Title = story.GetProperty("title").GetString() ?? "",
                        Url = story.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                        Score = story.TryGetProperty("score", out var score) ? score.GetInt32() : 0
                    });
                }
            }
        }
        catch { }
        return items;
    }

    private async Task<List<VeilleItem>> GetRedditTrendingAsync(int limit, CancellationToken ct)
    {
        var items = new List<VeilleItem>();
        try
        {
            var json = await _httpClient.GetStringAsync("https://www.reddit.com/r/programming/hot.json?limit=" + limit, ct);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            var children = doc.GetProperty("data").GetProperty("children");

            foreach (var child in children.EnumerateArray())
            {
                if (items.Count >= limit) break;
                var data = child.GetProperty("data");
                items.Add(new VeilleItem
                {
                    Source = "Reddit",
                    Title = data.GetProperty("title").GetString() ?? "",
                    Url = data.GetProperty("url").GetString() ?? "",
                    Score = data.GetProperty("score").GetInt32()
                });
            }
        }
        catch { }
        return items;
    }

    private async Task<List<VeilleItem>> GetSourceItemsAsync(string source, CancellationToken ct)
    {
        return source.ToLowerInvariant() switch
        {
            "github" => await GetGithubTrendingAsync(10, ct),
            "hn" or "hackernews" => await GetHackerNewsAsync(10, ct),
            "reddit" => await GetRedditTrendingAsync(10, ct),
            "devto" or "dev.to" => await GetDevToTrendingAsync(10, ct),
            "stackoverflow" => await GetStackOverflowTrendingAsync(10, ct),
            "arxiv" => await ParseRssAsync("http://export.arxiv.org/api/query?search_query=cat:cs.AI&sortBy=submittedDate&max_results=10", "arXiv", 10, ct),
            "lobsters" => await ParseRssAsync("https://lobste.rs/newest.rss", "Lobsters", 10, ct),
            "slashdot" => await ParseRssAsync("https://rss.slashdot.org/Slashdot/slashdotMain", "Slashdot", 10, ct),
            "thehackernews" or "thn" => await ParseRssAsync("https://feeds.feedburner.com/TheHackerNews", "TheHackerNews", 10, ct),
            "medium" => await ParseRssAsync("https://medium.com/feed/tag/artificial-intelligence", "Medium", 10, ct),
            _ => new List<VeilleItem>()
        };
    }

    private async Task<List<VeilleItem>> GetDevToTrendingAsync(int limit, CancellationToken ct)
    {
        var items = new List<VeilleItem>();
        try
        {
            var json = await _httpClient.GetStringAsync("https://dev.to/api/articles?per_page=20&top=7", ct);
            var articles = JsonSerializer.Deserialize<List<JsonElement>>(json);

            if (articles is not null)
            {
                foreach (var article in articles)
                {
                    if (items.Count >= limit) break;
                    items.Add(new VeilleItem
                    {
                        Source = "Dev.to",
                        Title = article.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                        Url = article.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                        Description = article.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null ? desc.GetString() : null,
                        Score = article.TryGetProperty("positive_reactions_count", out var score) ? score.GetInt32() : 0
                    });
                }
            }
        }
        catch { }
        return items;
    }

    private async Task<List<VeilleItem>> GetStackOverflowTrendingAsync(int limit, CancellationToken ct)
    {
        var items = new List<VeilleItem>();
        try
        {
            var json = await _httpClient.GetStringAsync("https://api.stackexchange.com/2.3/questions?order=desc&sort=votes&site=stackoverflow&pagesize=20", ct);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            if (doc.TryGetProperty("items", out var arr))
            {
                foreach (var question in arr.EnumerateArray())
                {
                    if (items.Count >= limit) break;
                    var tags = question.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", tagsProp.EnumerateArray().Select(t => t.GetString()))
                        : null;
                    items.Add(new VeilleItem
                    {
                        Source = "StackOverflow",
                        Title = question.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                        Url = question.TryGetProperty("link", out var link) ? link.GetString() ?? "" : "",
                        Description = string.IsNullOrEmpty(tags) ? null : tags,
                        Score = question.TryGetProperty("score", out var score) ? score.GetInt32() : 0
                    });
                }
            }
        }
        catch { }
        return items;
    }

    private async Task<List<VeilleItem>> ParseRssAsync(string url, string sourceName, int limit, CancellationToken ct)
    {
        var items = new List<VeilleItem>();
        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return items;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = XmlReader.Create(stream);

            string? title = null;
            string? link = null;
            string? description = null;
            string? pubDate = null;
            var inItem = false;

            while (reader.Read())
            {
                if (ct.IsCancellationRequested) break;

                if (reader.NodeType == XmlNodeType.Element)
                {
                    var name = reader.LocalName.ToLowerInvariant();
                    if (name is "item" or "entry")
                    {
                        inItem = true;
                        title = null;
                        link = null;
                        description = null;
                        pubDate = null;
                    }
                    else if (inItem)
                    {
                        if (name is "title" or "link" or "id" or "description" or "summary" or "pubdate" or "updated")
                        {
                            if (name == "link" && reader.IsEmptyElement)
                            {
                                var href = reader.GetAttribute("href");
                                if (link is null && !string.IsNullOrEmpty(href))
                                    link = href;
                            }
                            else
                            {
                                var value = await ReadElementTextAsync(reader);
                                if (name == "title") title = value;
                                else if (name is "link" or "id") if (link is null) link = value;
                                else if (name is "description" or "summary") description = value;
                                else if (name is "pubdate" or "updated") pubDate = value;
                            }
                        }
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "item" or "entry")
                {
                    if (!string.IsNullOrEmpty(title))
                    {
                        items.Add(new VeilleItem
                        {
                            Source = sourceName,
                            Title = StripTags(title),
                            Url = link ?? "",
                            Description = string.IsNullOrEmpty(description) ? null : StripTags(description),
                            FoundAt = ParseDate(pubDate)
                        });
                    }

                    title = null;
                    link = null;
                    description = null;
                    pubDate = null;
                    inItem = false;

                    if (items.Count >= limit) break;
                }
            }
        }
        catch { }
        return items;
    }

    private static async Task<string> ReadElementTextAsync(XmlReader reader)
    {
        if (reader.IsEmptyElement) return "";
        var sb = new System.Text.StringBuilder();
        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Text ||
                reader.NodeType == XmlNodeType.CDATA)
            {
                sb.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "img")
            {
                sb.Append(reader.GetAttribute("src"));
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                break;
            }
        }
        return sb.ToString();
    }

    private static string StripTags(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = Regex.Replace(value, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    private static DateTime ParseDate(string? pubDate)
    {
        if (DateTime.TryParse(pubDate, out var parsed))
            return parsed;
        return DateTime.UtcNow;
    }

    private void SaveHistory(List<VeilleItem> items)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var existing = new List<VeilleItem>();
            if (File.Exists(_storagePath))
                existing = JsonSerializer.Deserialize<List<VeilleItem>>(File.ReadAllText(_storagePath)) ?? new();

            existing.AddRange(items);
            if (existing.Count > 500) existing = existing.TakeLast(500).ToList();

            File.WriteAllText(_storagePath, JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public sealed class VeilleResult
{
    public bool Success { get; set; }
    public List<VeilleItem> Items { get; set; } = new();
    public int SourcesCollected { get; set; }
    public List<string> Errors { get; set; } = new();
    public DateTime CollectedAt { get; set; }
}

public sealed class VeilleItem
{
    public string Source { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Description { get; set; }
    public int Score { get; set; }
    public DateTime FoundAt { get; set; } = DateTime.UtcNow;
}
