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
    Task<ScrapeResult> DownloadPageImagesAsync(string url, string outputDir, CancellationToken ct = default);
    Task<SiteReportResult> GenerateSiteReportAsync(ScrapeResult site, CancellationToken ct = default);
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
        var allImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                    foreach (var img in pageResult.Images)
                        allImages.Add(img);

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
        result.Images = allImages.ToList();
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

    public async Task<ScrapeResult> DownloadPageImagesAsync(string url, string outputDir, CancellationToken ct = default)
    {
        var result = new ScrapeResult { Url = url, OutputDirectory = outputDir };

        try
        {
            var html = await _httpClient.GetStringAsync(url, ct);
            var images = ExtractImagesFromHtml(html, url);
            var uniqueImages = images.ToHashSet(StringComparer.OrdinalIgnoreCase).ToList();

            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var imgUrl in uniqueImages)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var uri = new Uri(imgUrl);
                    var fileBase = System.Text.RegularExpressions.Regex.Replace(
                        Path.GetFileNameWithoutExtension(uri.AbsolutePath) ?? "", "[^A-Za-z0-9._-]", "_");
                    var ext = Path.GetExtension(uri.AbsolutePath);
                    if (string.IsNullOrWhiteSpace(ext) || ext.Length > 5) ext = ".jpg";
                    if (string.IsNullOrWhiteSpace(fileBase)) fileBase = "image";

                    var target = fileBase + ext;
                    var counter = 1;
                    while (usedNames.Contains(target))
                    {
                        target = $"{fileBase}_{counter}{ext}";
                        counter++;
                    }
                    usedNames.Add(target);

                    var filePath = Path.Combine(outputDir, target);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(20));
                    using var response = await _httpClient.GetAsync(imgUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    response.EnsureSuccessStatusCode();

                    var lengthOk = (response.Content.Headers.ContentLength ?? 0) <= 5L * 1024 * 1024;
                    if (!lengthOk)
                    {
                        result.ImagesFailed++;
                        continue;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);

                    var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, cts.Token);
                    if (buffer.Length > 5L * 1024 * 1024)
                    {
                        result.ImagesFailed++;
                        continue;
                    }

                    await File.WriteAllBytesAsync(filePath, buffer.ToArray(), cts.Token);
                    result.ImagesDownloaded++;
                }
                catch
                {
                    result.ImagesFailed++;
                }
            }

            result.Success = true;
            _logger.LogInformation("[Scraper] Downloaded {Count} images to {Dir}", result.ImagesDownloaded, outputDir);
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Téléchargement annulé";
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            result.Success = false;
            _logger.LogWarning(ex, "[Scraper] Image download failed: {Url}", url);
        }

        return result;
    }

    public Task<SiteReportResult> GenerateSiteReportAsync(ScrapeResult site, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Rapport d'exploration du site");
        sb.AppendLine($"URL de départ : {site.Url}");
        sb.AppendLine();
        sb.AppendLine($"- Pages explorées : {site.PagesScraped}");
        sb.AppendLine($"- Titre principal : {site.Title}");
        sb.AppendLine($"- Liens trouvés : {site.Links.Count}");
        sb.AppendLine($"- Images trouvées : {site.Images.Count}");
        sb.AppendLine();

        if (site.Links.Count > 0)
        {
            sb.AppendLine("## Pages (top 50)");
            var pages = site.Links
                .Where(l => l.IsInternal)
                .GroupBy(l => l.Url)
                .Select(g => (Url: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count)
                .Take(50);

            foreach (var page in pages)
                sb.AppendLine($"- {page.Url}");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(site.ErrorMessage))
        {
            sb.AppendLine("## Erreurs");
            sb.AppendLine($"- {site.ErrorMessage}");
        }

        _logger.LogInformation("[Scraper] Rapport généré pour {Url} ({Pages} pages)", site.Url, site.PagesScraped);
        return Task.FromResult(new SiteReportResult { Success = true, Report = sb.ToString() });
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
    public int ImagesDownloaded { get; set; }
    public int ImagesFailed { get; set; }
    public string? OutputDirectory { get; set; }
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

public sealed class SiteReportResult
{
    public bool Success { get; set; }
    public string Report { get; set; } = "";
    public string? OutputPath { get; set; }
    public string? ErrorMessage { get; set; }
}
