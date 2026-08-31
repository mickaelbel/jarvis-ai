using Microsoft.Extensions.Logging;
using System.Text.Json;

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

        foreach (var source in sources)
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
            _ => new List<VeilleItem>()
        };
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
