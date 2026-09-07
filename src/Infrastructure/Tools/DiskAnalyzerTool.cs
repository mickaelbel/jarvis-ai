using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class DiskAnalyzerTool : ToolBase
{
    private readonly IDiskAnalyzerService _service;
    private readonly ILogger<DiskAnalyzerTool> _logger;

    public override string Name => "disk_analyzer";
    public override string Description => "Analyser l'utilisation du disque. Usage: disk_analyzer(action: \"report\") ou disk_analyzer(action: \"files\", path: \"C:/\", limit: \"20\", min_size_mb: \"10\"). Actions : report, files, folders.";
    public override string Category => "system";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "report (défaut), files, folders", typeof(string)),
        new ToolParameter("path", "Chemin à analyser (défaut C:\\)", typeof(string)),
        new ToolParameter("limit", "Nombre max de résultats (défaut 20)", typeof(string)),
        new ToolParameter("min_size_mb", "Taille minimale en Mo (défaut 10)", typeof(string))
    };

    public DiskAnalyzerTool(IDiskAnalyzerService service, ILogger<DiskAnalyzerTool> logger)
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
        parameters.TryGetValue("action", out var actionStr);
        parameters.TryGetValue("path", out var path);
        parameters.TryGetValue("limit", out var limitStr);
        parameters.TryGetValue("min_size_mb", out var minSizeStr);

        var action = (actionStr ?? "report").ToLowerInvariant();
        var targetPath = string.IsNullOrWhiteSpace(path) ? @"C:\" : path!;

        var limit = 20;
        if (int.TryParse(limitStr, out var parsedLimit) && parsedLimit > 0)
            limit = parsedLimit;

        var minSizeMb = 10L;
        if (long.TryParse(minSizeStr, out var parsedMin) && parsedMin > 0)
            minSizeMb = parsedMin;

        return action switch
        {
            "files" => await FilesAsync(targetPath, limit, minSizeMb, ct),
            "folders" => await FoldersAsync(targetPath, limit, ct),
            "report" => await ReportAsync(ct),
            _ => Fail($"Action inconnue : '{action}'. Actions valides : report, files, folders")
        };
    }

    private async Task<ToolResult> FilesAsync(string path, int limit, long minSizeMb, CancellationToken ct)
    {
        var files = await _service.GetLargestFilesAsync(path, limit, minSizeMb, ct);
        if (files.Count == 0)
            return Ok($"Aucun fichier trouvé dans : {path}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Plus gros fichiers dans {path} :");
        foreach (var f in files)
            sb.AppendLine($"  {FormatSize(f.SizeBytes)} — {f.Path} — modifié {f.LastModified:dd/MM/yyyy}");
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> FoldersAsync(string path, int limit, CancellationToken ct)
    {
        var folders = await _service.GetLargestFoldersAsync(path, limit, ct);
        if (folders.Count == 0)
            return Ok($"Aucun dossier trouvé dans : {path}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Plus gros dossiers dans {path} :");
        foreach (var f in folders)
            sb.AppendLine($"  {FormatSize(f.SizeBytes)} ({f.FileCount} fichiers) — {f.Path}");
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> ReportAsync(CancellationToken ct)
    {
        var report = await _service.GetFullReportAsync(ct);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Rapport de disque :");
        sb.AppendLine($"  Utilisé : {FormatSize(report.UsedBytes)} / {FormatSize(report.TotalBytes)} ({report.UsagePercent}%)");
        sb.AppendLine($"  Libre : {FormatSize(report.FreeBytes)}");
        sb.AppendLine();
        sb.AppendLine("Plus gros dossiers :");
        foreach (var f in report.LargestFolders)
            sb.AppendLine($"  {FormatSize(f.SizeBytes)} — {f.Path}");
        sb.AppendLine();
        sb.AppendLine("Plus gros fichiers :");
        foreach (var f in report.LargestFiles)
            sb.AppendLine($"  {FormatSize(f.SizeBytes)} — {f.Path}");
        return Ok(sb.ToString());
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
