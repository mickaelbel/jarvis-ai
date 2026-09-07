using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class DownloadManagerTool : ToolBase
{
    private readonly IDownloadManagerService _service;
    private readonly ILogger<DownloadManagerTool> _logger;

    public override string Name => "download_manager";
    public override string Description => "Gérer les téléchargements : un fichier, lot, statut, annulation. Usage: download_manager(action: \"download\", url: \"https://example.com/file.zip\", output_dir: \"C:/downloads\"). Actions : download, batch, status, cancel.";
    public override string Category => "web";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "download, batch, status, cancel (requis)", typeof(string), required: true),
        new ToolParameter("url", "URL du fichier à télécharger", typeof(string)),
        new ToolParameter("output_dir", "Répertoire de sortie", typeof(string)),
        new ToolParameter("urls", "Liste d'URLs séparées par des virgules (pour batch)", typeof(string)),
        new ToolParameter("max_parallel", "Téléchargements parallèles max (défaut 5)", typeof(string))
    };

    public DownloadManagerTool(IDownloadManagerService service, ILogger<DownloadManagerTool> logger)
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
        var action = RequireParam(parameters, "action").ToLowerInvariant();
        parameters.TryGetValue("url", out var url);
        parameters.TryGetValue("output_dir", out var outputDir);
        parameters.TryGetValue("urls", out var urlsStr);
        parameters.TryGetValue("max_parallel", out var maxParallelStr);

        return action switch
        {
            "download" => await DownloadAsync(url, outputDir, ct),
            "batch" => await BatchAsync(urlsStr, outputDir, maxParallelStr, ct),
            "status" => await StatusAsync(),
            "cancel" => await CancelAsync(url),
            _ => Fail($"Action inconnue : '{action}'. Actions valides : download, batch, status, cancel")
        };
    }

    private async Task<ToolResult> DownloadAsync(string? url, string? outputDir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Fail("Le paramètre 'url' est requis pour l'action download.");

        var result = await _service.DownloadFileAsync(url, outputDir, ct: ct);
        if (!result.Success)
            return Fail($"Échec du téléchargement : {result.ErrorMessage}");

        return Ok($"Téléchargement terminé.\nFichier : {result.OutputPath}\nTaille : {FormatSize(result.FileSizeBytes)}");
    }

    private async Task<ToolResult> BatchAsync(string? urlsStr, string? outputDir, string? maxParallelStr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(urlsStr))
            return Fail("Le paramètre 'urls' est requis pour l'action batch.");

        var urls = urlsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (urls.Length == 0)
            return Fail("Aucune URL fournie.");

        var maxParallel = 5;
        if (int.TryParse(maxParallelStr, out var parsed) && parsed > 0)
            maxParallel = parsed;

        var result = await _service.DownloadBatchAsync(urls, outputDir, maxParallel, ct: ct);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Téléchargement par lot terminé.");
        sb.AppendLine($"Réussis : {result.FilesDownloaded}");
        sb.AppendLine($"Échecs : {result.Errors.Count}");
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> StatusAsync()
    {
        var active = await _service.GetActiveDownloads();
        if (active.Count == 0)
            return Ok("Aucun téléchargement actif.");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Téléchargements actifs : {active.Count}");
        foreach (var dl in active)
            sb.AppendLine($"  - ID: {dl.Id}");
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> CancelAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Fail("Le paramètre 'url' est requis pour annuler.");

        var cancelled = await _service.CancelDownloadAsync(url);
        return cancelled
            ? Ok($"Téléchargement annulé : {url}")
            : Fail($"Aucun téléchargement trouvé pour : {url}");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
