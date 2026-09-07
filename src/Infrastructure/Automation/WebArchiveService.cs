using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.Automation;

public interface IWebArchiveService
{
    Task<WebArchiveResult> ArchivePageWithAssetsAsync(string url, string outputDir, CancellationToken ct = default);
}

public sealed class WebArchiveService : IWebArchiveService
{
    private readonly ILogger<WebArchiveService> _logger;
    private readonly HttpClient _httpClient;

    public WebArchiveService(ILogger<WebArchiveService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public async Task<WebArchiveResult> ArchivePageWithAssetsAsync(string url, string outputDir, CancellationToken ct = default)
    {
        var result = new WebArchiveResult { Url = url };
        var errors = new List<string>();

        try
        {
            var html = await _httpClient.GetStringAsync(url, ct);

            var baseUri = new Uri(url);
            var cssUrls = ExtractUrls(html, "<link[^>]+rel=[\"']stylesheet[\"'][^>]+href=[\"']([^\"']+)[\"']", baseUri);
            cssUrls.AddRange(ExtractUrls(html, "<link[^>]+href=[\"']([^\"']+)[\"'][^>]+rel=[\"']stylesheet[\"']", baseUri));
            var jsUrls = ExtractUrls(html, "<script[^>]+src=[\"']([^\"']+)[\"']", baseUri);
            var imgUrls = ExtractUrls(html, "<img[^>]+src=[\"']([^\"']+)[\"']", baseUri);

            Directory.CreateDirectory(outputDir);
            var assetRoot = Path.Combine(outputDir, "assets");
            Directory.CreateDirectory(Path.Combine(assetRoot, "css"));
            Directory.CreateDirectory(Path.Combine(assetRoot, "js"));
            Directory.CreateDirectory(Path.Combine(assetRoot, "img"));

            var cssMap = await DownloadAssetsAsync(cssUrls.DistinctBy(p => p.absolute), "css", Path.Combine(assetRoot, "css"), ct, errors);
            var jsMap = await DownloadAssetsAsync(jsUrls.DistinctBy(p => p.absolute), "js", Path.Combine(assetRoot, "js"), ct, errors);
            var imgMap = await DownloadAssetsAsync(imgUrls.DistinctBy(p => p.absolute), "img", Path.Combine(assetRoot, "img"), ct, errors);

            html = RewriteUrls(html, cssMap);
            html = RewriteUrls(html, jsMap);
            html = RewriteUrls(html, imgMap);

            var indexPath = Path.Combine(outputDir, "index.html");
            await File.WriteAllTextAsync(indexPath, html, ct);

            result.Success = true;
            result.OutputPath = indexPath;
            result.AssetsDownloaded = cssMap.Count + jsMap.Count + imgMap.Count;
            result.AssetsFailed = errors.Count;
            result.Errors = errors;

            _logger.LogInformation("[WebArchive] Archived: {Url} → {Path} ({Assets} assets)", url, indexPath, result.AssetsDownloaded);
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Archive cancelled";
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            result.Errors = errors;
            _logger.LogError(ex, "[WebArchive] Failed: {Url}", url);
        }

        return result;
    }

    private static List<(string original, string absolute)> ExtractUrls(string html, string pattern, Uri baseUri)
    {
        var urls = new List<(string original, string absolute)>();
        foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase))
        {
            try
            {
                var abs = new Uri(baseUri, match.Groups[1].Value).ToString();
                urls.Add((match.Groups[1].Value, abs));
            }
            catch { }
        }
        return urls;
    }

    private async Task<Dictionary<string, string>> DownloadAssetsAsync(
        IEnumerable<(string original, string absolute)> assets, string type, string dir, CancellationToken ct, List<string> errors)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            if (ct.IsCancellationRequested) break;
            if (map.ContainsKey(asset.original)) continue;

            try
            {
                var stripped = StripQueryFragment(asset.absolute);
                var ext = GetAssetExtension(stripped, type);
                var baseName = Path.GetFileNameWithoutExtension(new Uri(stripped).AbsolutePath);
                if (string.IsNullOrWhiteSpace(baseName)) baseName = "asset";
                baseName = Regex.Replace(baseName, "[^A-Za-z0-9._-]", "_");
                if (baseName.Length > 40) baseName = baseName[..40];
                baseName = baseName.Trim('.');

                var fileName = baseName + "." + ext;
                var counter = 1;
                while (usedNames.Contains(fileName))
                {
                    fileName = $"{baseName}_{counter}.{ext}";
                    counter++;
                }
                usedNames.Add(fileName);

                var localPath = Path.Combine(dir, fileName);
                var relative = $"assets/{type}/{fileName}";

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await _httpClient.GetAsync(asset.absolute, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                await using var file = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(file, cts.Token);

                map[asset.original] = relative;
            }
            catch (Exception ex)
            {
                errors.Add($"{type}: {asset.original} -> {ex.Message}");
            }
        }

        return map;
    }

    private static string GetAssetExtension(string url, string type)
    {
        var path = new Uri(url).AbsolutePath;
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (!string.IsNullOrEmpty(ext) && ext.Length <= 5) return ext;
        return type switch
        {
            "css" => "css",
            "js" => "js",
            _ => "img"
        };
    }

    private static string StripQueryFragment(string url)
    {
        var idx = url.IndexOfAny(new[] { '?', '#' });
        return idx < 0 ? url : url[..idx];
    }

    private static string RewriteUrls(string html, Dictionary<string, string> map)
    {
        foreach (var kv in map)
        {
            var escaped = Regex.Escape(kv.Key);
            html = Regex.Replace(html, $"{escaped}(\"|'|>|\\s)",
                m => kv.Value + m.Groups[1].Value,
                RegexOptions.IgnoreCase);
        }
        return html;
    }
}

public sealed class WebArchiveResult
{
    public bool Success { get; set; }
    public string Url { get; set; } = "";
    public string? OutputPath { get; set; }
    public int AssetsDownloaded { get; set; }
    public int AssetsFailed { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
