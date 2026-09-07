using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class FolderCompareTool : ToolBase
{
    private readonly IFolderCompareService _service;
    private readonly ILogger<FolderCompareTool> _logger;

    public override string Name => "folder_compare";
    public override string Description => "Comparer deux dossiers et lister fichiers manquants ou modifiés. Usage: folder_compare(folder_a: \"C:/A\", folder_b: \"C:/B\")";
    public override string Category => "files";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("folder_a", "Premier dossier", typeof(string), required: true),
        new ToolParameter("folder_b", "Second dossier", typeof(string), required: true)
    };

    public FolderCompareTool(IFolderCompareService service, ILogger<FolderCompareTool> logger)
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
        var folderA = RequireParam(parameters, "folder_a");
        var folderB = RequireParam(parameters, "folder_b");

        var result = await _service.CompareFoldersAsync(folderA, folderB, ct);
        if (!result.Success)
            return Fail($"Échec de la comparaison : {result.ErrorMessage}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Comparaison : {folderA} vs {folderB}");
        sb.AppendLine($"Fichiers dans A : {result.TotalFilesPath1}, dans B : {result.TotalFilesPath2}");
        sb.AppendLine();
        sb.AppendLine($"Fichiers uniquement dans A ({result.FilesOnlyInPath1.Count}) :");
        foreach (var f in result.FilesOnlyInPath1.Take(50))
            sb.AppendLine($"  - {f}");
        sb.AppendLine();
        sb.AppendLine($"Fichiers uniquement dans B ({result.FilesOnlyInPath2.Count}) :");
        foreach (var f in result.FilesOnlyInPath2.Take(50))
            sb.AppendLine($"  - {f}");
        sb.AppendLine();
        sb.AppendLine($"Fichiers modifiés ({result.ModifiedFiles.Count}) :");
        foreach (var m in result.ModifiedFiles.Take(50))
            sb.AppendLine($"  - {m.RelativePath} (A: {m.Path1Size} B, {m.Path1LastModified:dd/MM/yyyy HH:mm} | B: {m.Path2Size} B, {m.Path2LastModified:dd/MM/yyyy HH:mm})");
        return Ok(sb.ToString());
    }
}
