using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class PhotoOrganizerTool : ToolBase
{
    private readonly IPhotoOrganizerService _service;
    private readonly ILogger<PhotoOrganizerTool> _logger;

    public override string Name => "photo_organizer";
    public override string Description => "Organiser des photos par date ou supprimer leurs métadonnées. Usage: photo_organizer(action: \"organize\", source_folder: \"C:/photos\", destination_root: \"C:/tri\"). Actions : organize, strip.";
    public override string Category => "files";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;
    public override TimeSpan Timeout => TimeSpan.FromMinutes(30);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "organize ou strip (requis)", typeof(string), required: true),
        new ToolParameter("source_folder", "Dossier des photos (requis)", typeof(string), required: true),
        new ToolParameter("destination_root", "Dossier de destination racine (défaut : source_folder)", typeof(string))
    };

    public PhotoOrganizerTool(IPhotoOrganizerService service, ILogger<PhotoOrganizerTool> logger)
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
        var sourceFolder = RequireParam(parameters, "source_folder");
        parameters.TryGetValue("destination_root", out var destinationRoot);

        return action switch
        {
            "organize" => await OrganizeAsync(sourceFolder, destinationRoot, ct),
            "strip" => await StripAsync(sourceFolder, ct),
            _ => Fail($"Action inconnue : '{action}'. Actions valides : organize, strip")
        };
    }

    private async Task<ToolResult> OrganizeAsync(string sourceFolder, string? destinationRoot, CancellationToken ct)
    {
        var destRoot = string.IsNullOrWhiteSpace(destinationRoot) ? sourceFolder : destinationRoot;
        var result = await _service.OrganizeByDateAsync(sourceFolder, destRoot, ct);
        if (!result.Success)
            return Fail($"Échec de l'organisation : {result.ErrorMessage}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Organisation des photos terminée.");
        sb.AppendLine($"Fichiers traités : {result.FilesProcessed}");
        sb.AppendLine($"Organisés par métadonnées EXIF : {result.OrganizedByExif}");
        sb.AppendLine($"Organisés par date de fichier : {result.OrganizedByFileDate}");
        sb.AppendLine($"Destination : {destRoot}");
        if (result.Errors.Count > 0)
        {
            sb.AppendLine($"Erreurs : {result.Errors.Count}");
            foreach (var error in result.Errors.Take(10))
                sb.AppendLine($"  - {error}");
        }
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> StripAsync(string folder, CancellationToken ct)
    {
        var result = await _service.StripMetadataAsync(folder, ct);
        if (!result.Success)
            return Fail($"Échec du retrait des métadonnées : {result.ErrorMessage}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Suppression des métadonnées terminée.");
        sb.AppendLine($"Fichiers traités : {result.FilesProcessed}");
        sb.AppendLine($"Dossier : {folder}");
        if (result.Errors.Count > 0)
        {
            sb.AppendLine($"Erreurs : {result.Errors.Count}");
            foreach (var error in result.Errors.Take(10))
                sb.AppendLine($"  - {error}");
        }
        return Ok(sb.ToString());
    }
}
