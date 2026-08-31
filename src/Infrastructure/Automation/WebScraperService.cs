using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Automation;

public interface IWebScraperService
{
    Task<ScrapeResult> ScrapePageAsync(string url, CancellationToken ct = default);
    Task<ScrapeResult> ScrapeFullSiteAsync(string baseUrl, int maxPages = 100, CancellationToken ct = default);
    Task<List<ScrapedElement>> ExtractElementsAsync(string url, string selector, CancellationToken ct = default);
    Task<List<ScrapedLink>> ExtractLinksAsync(string url, CancellationToken ct = default);
    Task<string> ExtractTextAsync(string url, CancellationToken ct = default);
}

public sealed class WebScraperService : IWebScraperService
{
    private readonly ILogger<WebScraperService> _logger;
    private readonly HttpClient _httpClient;

    public WebScraperService(ILogger<WebScraperService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public async Task<ScrapeResult> ScrapePageAsync(string url, CancellationToken ct = default)
    {
        var result = new ScrapeResult { Url = url };

        try
        {
            var html = await _httpClient.GetStringAsync(url, ct);
            result.Html = html;
            result.Title = ExtractTitle(html);
            result.Text = HtmlToText(html);
            result.Links = ExtractLinksFromHtml(html, url);
            result.Images = ExtractImagesFromHtml(html, url);
            result.Success = true;

            _logger.LogInformation("[Scraper] Scraped: {Url} ({Length} chars)", url, html.Length);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogWarning(ex, "[Scraper] Failed: {Url}", url);
        }

        return result;
    }

    public async Task<ScrapeResult> ScrapeFullSiteAsync(string baseUrl, int maxPages = 100, CancellationToken ct = default)
    {
        var result = new ScrapeResult { Url = baseUrl };
        var visited = new HashSet<string>();
        var toVisit = new Queue<string>();
        var allContent = new List<string>();

        toVisit.Enqueue(baseUrl);
        var baseUri = new Uri(baseUrl);

        while (toVisit.Count > 0 && visited.Count < maxPages && !ct.IsCancellationRequested)
        {
            var currentUrl = toVisit.Dequeue();

            if (visited.Contains(currentUrl))
                continue;

            visited.Add(currentUrl);

            try
            {
                var pageResult = await ScrapePageAsync(currentUrl, ct);
                if (pageResult.Success)
                {
                    allContent.Add(pageResult.Text);

                    // Add same-domain links to queue
                    foreach (var linkItem in pageResult.Links.Where(l => l.IsInternal))
                    {
                        if (!visited.Contains(linkItem.Url))
                            toVisit.Enqueue(linkItem.Url);
                    }
                }

                await Task.Delay(500, ct); // Rate limiting
            }
            catch { }
        }

        result.Text = string.Join("\n\n---\n\n", allContent);
        result.PagesScraped = visited.Count;
        result.Success = true;

        _logger.LogInformation("[Scraper] Full site scraped: {Pages} pages from {Url}", visited.Count, baseUrl);
        return result;
    }

    public async Task<List<ScrapedElement>> ExtractElementsAsync(string url, string selector, CancellationToken ct = default)
    {
        var elements = new List<ScrapedElement>();

        try
        {
            var html = await _httpClient.GetStringAsync(url, ct);

            // Simple HTML parsing (no external dependency)
            var startIndex = 0;
            while (startIndex < html.Length)
            {
                var tagStart = html.IndexOf('<', startIndex);
                if (tagStart < 0) break;

                var tagEnd = html.IndexOf('>', tagStart);
                if (tagEnd < 0) break;

                var tag = html[(tagStart + 1)..tagEnd].Split(' ')[0].ToLower();

                if (tag == selector.ToLower().TrimStart('<', '>'))
                {
                    var contentStart = tagEnd + 1;
                    var contentEnd = html.IndexOf("</" + tag, contentStart);
                    if (contentEnd > contentStart)
                    {
                        var content = html[contentStart..contentEnd].Trim();
                        elements.Add(new ScrapedElement
                        {
                            Tag = tag,
                            Content = content,
                            Attributes = ExtractAttributes(html[(tagStart + 1)..tagEnd])
                        });
                    }
                }

                startIndex = tagEnd + 1;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Scraper] Extract failed: {Url}", url);
        }

        return elements;
    }

    public async Task<List<ScrapedLink>> ExtractLinksAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(url, ct);
            return ExtractLinksFromHtml(html, url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Scraper] Extract links failed: {Url}", url);
            return new List<ScrapedLink>();
        }
    }

    public async Task<string> ExtractTextAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(url, ct);
            return HtmlToText(html);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Scraper] Extract text failed: {Url}", url);
            return "";
        }
    }

    private static string ExtractTitle(string html)
    {
        var titleStart = html.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
        var titleEnd = html.IndexOf("</title>", StringComparison.OrdinalIgnoreCase);

        if (titleStart >= 0 && titleEnd > titleStart)
            return html[(titleStart + 7)..titleEnd].Trim();

        return "";
    }

    private static string HtmlToText(string html)
    {
        // Remove scripts and styles
        html = System.Text.RegularExpressions.Regex.Replace(html, "<script[^>]*>[\\s\\S]*?</script>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, "<style[^>]*>[\\s\\S]*?</style>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove tags
        html = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");

        // Decode HTML entities
        html = System.Net.WebUtility.HtmlDecode(html);

        // Clean up whitespace
        html = System.Text.RegularExpressions.Regex.Replace(html, "\\s+", " ").Trim();

        return html;
    }

    private static List<ScrapedLink> ExtractLinksFromHtml(string html, string baseUrl)
    {
        var links = new List<ScrapedLink>();
        var baseUri = new Uri(baseUrl);

        var matches = System.Text.RegularExpressions.Regex.Matches(html, "<a[^>]+href=[\"']([^\"']+)[\"'][^>]*>(.*?)</a>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (match.Groups.Count >= 3)
            {
                var href = match.Groups[1].Value;
                var text = System.Text.RegularExpressions.Regex.Replace(match.Groups[2].Value, "<[^>]+>", "").Trim();

                try
                {
                    var linkUri = new Uri(baseUri, href);
                    links.Add(new ScrapedLink
                    {
                        Url = linkUri.ToString(),
                        Text = text,
                        IsInternal = linkUri.Host == baseUri.Host
                    });
                }
                catch { }
            }
        }

        return links;
    }

    private static List<string> ExtractImagesFromHtml(string html, string baseUrl)
    {
        var images = new List<string>();

        var matches = System.Text.RegularExpressions.Regex.Matches(html, "<img[^>]+src=[\"']([^\"']+)[\"']",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (match.Groups.Count >= 2)
            {
                var src = match.Groups[1].Value;
                try
                {
                    var imgUri = new Uri(new Uri(baseUrl), src);
                    images.Add(imgUri.ToString());
                }
                catch { }
            }
        }

        return images;
    }

    private static Dictionary<string, string> ExtractAttributes(string tagContent)
    {
        var attrs = new Dictionary<string, string>();
        var matches = System.Text.RegularExpressions.Regex.Matches(tagContent, @"(\w+)=""([^""]*)""");

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (match.Groups.Count >= 3)
                attrs[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return attrs;
    }
}

public sealed class ScrapeResult
{
    public bool Success { get; set; }
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Html { get; set; }
    public string? Text { get; set; }
    public List<ScrapedLink> Links { get; set; } = new();
    public List<string> Images { get; set; } = new();
    public int PagesScraped { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class ScrapedElement
{
    public string Tag { get; set; } = "";
    public string Content { get; set; } = "";
    public Dictionary<string, string> Attributes { get; set; } = new();
}

public sealed class ScrapedLink
{
    public string Url { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsInternal { get; set; }
}
