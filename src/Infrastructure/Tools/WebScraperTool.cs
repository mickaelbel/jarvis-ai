using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class WebScraperTool : ToolBase
{
    private readonly IWebScraperService _service;
    private readonly ILogger<WebScraperTool> _logger;

    public override string Name => "web_scraper";
    public override string Description => "Scrape une page ou un site web complet. Usage: web_scraper(url: \"https://example.com\") ou web_scraper(url: \"https://example.com\", action: \"full\", max_pages: \"50\", download_images: \"true\", images_dir: \"C:/images\", report: \"true\"). Actions: page (une seule page), full (site complet), images (télécharger les images), report (rapport d'exploration).";
    public override string Category => "web";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("url", "URL de la page ou du site à scraper", typeof(string), required: true),
        new ToolParameter("action", "full (défaut), page, images, report", typeof(string)),
        new ToolParameter("max_pages", "Nombre max de pages à scraper (défaut 100)", typeof(string)),
        new ToolParameter("download_images", "Télécharger les images (true/false, défaut false)", typeof(string)),
        new ToolParameter("images_dir", "Répertoire de sortie des images", typeof(string)),
        new ToolParameter("report", "Générer un rapport d'exploration (true/false, défaut false)", typeof(string))
    };

    public WebScraperTool(IWebScraperService service, ILogger<WebScraperTool> logger)
        : base(logger)
    {
        _service = service;
        _logger = logger;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        var url = RequireParam(parameters, "url");
        parameters.TryGetValue("action", out var actionStr);
        parameters.TryGetValue("max_pages", out var maxPagesStr);
        parameters.TryGetValue("download_images", out var downloadImagesStr);
        parameters.TryGetValue("images_dir", out var imagesDir);
        parameters.TryGetValue("report", out var reportStr);

        var action = (actionStr ?? "full").ToLowerInvariant();
        var maxPages = 100;
        if (int.TryParse(maxPagesStr, out var parsedMax) && parsedMax > 0)
            maxPages = parsedMax;
        var downloadImages = downloadImagesStr?.ToLowerInvariant() is "true";
        var report = reportStr?.ToLowerInvariant() is "true";

        return action switch
        {
            "page" => await ScrapePageAsync(url, ct),
            "full" => await ScrapeFullAsync(url, maxPages, downloadImages, imagesDir, report, ct),
            "images" => await DownloadImagesAsync(url, imagesDir, ct),
            "report" => await ReportAsync(url, maxPages, ct),
            _ => Fail($"Action inconnue : '{action}'. Actions valides : page, full, images, report")
        };
    }

    private async Task<ToolResult> ScrapePageAsync(string url, CancellationToken ct)
    {
        var result = await _service.ScrapePageAsync(url, ct);
        if (!result.Success)
            return Fail($"Échec du scraping : {result.ErrorMessage}");

        var textPreview = result.Text?.Length > 300 ? result.Text[..300] + "..." : result.Text;
        return Ok($"Page scrapée : {result.Title}\nLiens : {result.Links.Count}\nImages : {result.Images.Count}\nTexte (aperçu) : {textPreview}");
    }

    private async Task<ToolResult> ScrapeFullAsync(string url, int maxPages, bool downloadImages, string? imagesDir, bool report, CancellationToken ct)
    {
        var result = await _service.ScrapeFullSiteAsync(url, maxPages, ct);
        if (!result.Success)
            return Fail($"Échec du scraping complet : {result.ErrorMessage}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Site scrapé : {result.Url}");
        sb.AppendLine($"Pages explorées : {result.PagesScraped}");
        sb.AppendLine($"Liens trouvés : {result.Links.Count}");
        sb.AppendLine($"Images trouvées : {result.Images.Count}");

        if (downloadImages)
        {
            var imgDir = imagesDir ?? Path.Combine(Path.GetTempPath(), "jarvis_images_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var imgResult = await _service.DownloadPageImagesAsync(url, imgDir, ct);
            sb.AppendLine($"Images téléchargées : {imgResult.ImagesDownloaded}/{imgResult.ImagesDownloaded + imgResult.ImagesFailed}");
            sb.AppendLine($"Répertoire images : {imgDir}");
        }

        if (report)
        {
            var siteReport = await _service.GenerateSiteReportAsync(result, ct);
            sb.AppendLine();
            sb.AppendLine(siteReport.Report);
        }

        return Ok(sb.ToString());
    }

    private async Task<ToolResult> DownloadImagesAsync(string url, string? imagesDir, CancellationToken ct)
    {
        var dir = imagesDir ?? Path.Combine(Path.GetTempPath(), "jarvis_images_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        var result = await _service.DownloadPageImagesAsync(url, dir, ct);
        if (!result.Success)
            return Fail($"Échec du téléchargement d'images : {result.ErrorMessage}");

        return Ok($"Images téléchargées : {result.ImagesDownloaded}\nÉchecs : {result.ImagesFailed}\nRépertoire : {dir}");
    }

    private async Task<ToolResult> ReportAsync(string url, int maxPages, CancellationToken ct)
    {
        var scrapeResult = await _service.ScrapeFullSiteAsync(url, maxPages, ct);
        var report = await _service.GenerateSiteReportAsync(scrapeResult, ct);
        return Ok(report.Report);
    }
}
