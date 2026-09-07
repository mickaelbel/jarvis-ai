using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class FileMaintenanceTool : ToolBase
{
    private readonly IFileDeduplicationService _dedupService;
    private readonly IFileCompressorService _compressorService;
    private readonly ILogger<FileMaintenanceTool> _logger;

    public override string Name => "file_maintenance";
    public override string Description => "Maintenance de fichiers : déduplication, analyse, compression. Usage: file_maintenance(action: \"analyze\", path: \"C:/docs\"). Actions : dedup, remove_duplicates, analyze, suggest, compress.";
    public override string Category => "files";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;
    public override TimeSpan Timeout => TimeSpan.FromMinutes(30);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "analyze, suggest, dedup, remove_duplicates, compress (requis)", typeof(string), required: true),
        new ToolParameter("path", "Chemin du fichier ou dossier à traiter (requis)", typeof(string), required: true),
        new ToolParameter("format", "Format de compression : zip (défaut), 7z, rar", typeof(string)),
        new ToolParameter("output_path", "Chemin du fichier compressé de sortie", typeof(string)),
        new ToolParameter("limit", "Limite de suggestions (défaut 10)", typeof(string)),
        new ToolParameter("strategy", "oldest (défaut), newest, smallest (pour remove_duplicates)", typeof(string))
    };

    public FileMaintenanceTool(IFileDeduplicationService dedupService, IFileCompressorService compressorService, ILogger<FileMaintenanceTool> logger)
        : base(logger)
    {
        _dedupService = dedupService;
        _compressorService = compressorService;
        _logger = logger;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        var action = RequireParam(parameters, "action").ToLowerInvariant();
        var path = RequireParam(parameters, "path");
        parameters.TryGetValue("format", out var format);
        parameters.TryGetValue("output_path", out var outputPath);
        parameters.TryGetValue("limit", out var limitStr);
        parameters.TryGetValue("strategy", out var strategy);

        var limit = 10;
        if (int.TryParse(limitStr, out var parsedLimit) && parsedLimit > 0)
            limit = parsedLimit;

        return action switch
        {
            "analyze" => await AnalyzeAsync(path, ct),
            "suggest" => await SuggestAsync(path, limit, ct),
            "dedup" => await DedupAsync(path, ct),
            "remove_duplicates" => await RemoveDuplicatesAsync(path, strategy, ct),
            "compress" => await CompressAsync(path, outputPath, format, ct),
            _ => Fail($"Action inconnue : '{action}'. Actions valides : analyze, suggest, dedup, remove_duplicates, compress")
        };
    }

    private async Task<ToolResult> AnalyzeAsync(string path, CancellationToken ct)
    {
        var report = await _compressorService.AnalyzeAsync(path, ct);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Analyse de : {path}");
        sb.AppendLine($"Fichiers scannés : {report.FilesScanned}");
        sb.AppendLine($"Taille totale : {FormatSize(report.TotalSizeBytes)}");
        sb.AppendLine($"Économie potentielle : {FormatSize(report.PotentialSavingsBytes)}");
        sb.AppendLine($"Candidats à la compression : {report.Candidates.Count}");
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> SuggestAsync(string path, int limit, CancellationToken ct)
    {
        var suggestions = await _compressorService.GetOptimizationSuggestionsAsync(path, limit, ct);
        if (suggestions.Count == 0)
            return Ok($"Aucune suggestion d'optimisation pour : {path}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Suggestions d'optimisation ({suggestions.Count}) :");
        foreach (var s in suggestions)
        {
            sb.AppendLine($"  {s.FilePath} ({FormatSize(s.SizeBytes)}) — suggestion : {s.Suggestion}");
        }
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> DedupAsync(string path, CancellationToken ct)
    {
        var report = await _dedupService.GetReportAsync(path, ct);
        return Ok($"Rapport de déduplication :\nGroupes de doublons : {report.TotalGroups}\nFichiers dupliqués : {report.TotalDuplicates}\nEspace gaspillé : {FormatSize(report.WastedBytes)}");
    }

    private async Task<ToolResult> RemoveDuplicatesAsync(string path, string? strategy, CancellationToken ct)
    {
        var groups = await _dedupService.FindDuplicatesAsync(path, ct: ct);
        if (groups.Count == 0)
            return Ok($"Aucun doublon trouvé dans : {path}");

        var removalStrategy = strategy?.ToLowerInvariant() switch
        {
            "newest" => DuplicateRemovalStrategy.KeepNewest,
            "smallest" => DuplicateRemovalStrategy.KeepSmallest,
            _ => DuplicateRemovalStrategy.KeepOldest
        };

        var removed = await _dedupService.RemoveDuplicatesAsync(groups, removalStrategy, ct);
        return Ok($"Doublons supprimés : {removed}");
    }

    private async Task<ToolResult> CompressAsync(string path, string? outputPath, string? format, CancellationToken ct)
    {
        var result = await _compressorService.CompressAsync(path, outputPath, format ?? "zip", ct: ct);
        if (!result.Success)
            return Fail($"Échec de la compression : {result.ErrorMessage}");

        return Ok($"Compression terminée.\nFichier : {result.OutputPath}\nFormat : {result.Format}\nTaille entrée : {FormatSize(result.InputBytes)}\nTaille sortie : {FormatSize(result.OutputBytes)}");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
