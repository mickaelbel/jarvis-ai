using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class BlenderAutomationTool : ITool
{
    private readonly IBlenderAutomationService _blender;
    private readonly ILogger<BlenderAutomationTool> _logger;

    public string Name => "blender_automation";
    public string Description =>
        "Automatise Blender : créer un fichier, supprimer le cube/lampe/caméra, enregistrer. " +
        "Actions : create_file (nouveau fichier), delete_cube (supprime cube/lampe/caméra par défaut), " +
        "save (enregistre le fichier), full_workflow (tout en une fois : nouveau fichier + supprime cube + enregistre).";
    public string Category => "bureau";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public string? WaitingPhrase => "J'automatise Blender...";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Action : create_file, delete_cube, save, full_workflow", typeof(string), required: true),
        new ToolParameter("type", "Type de fichier : general (défaut), video, vfx, sculpt", typeof(string), required: false),
        new ToolParameter("path", "Chemin d'enregistrement (pour action=save ou full_workflow)", typeof(string), required: false),
    };

    public BlenderAutomationTool(IBlenderAutomationService blender, ILogger<BlenderAutomationTool> logger)
    {
        _blender = blender;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("type", out var type);
        parameters.TryGetValue("path", out var path);

        if (string.IsNullOrWhiteSpace(action))
            return ToolResult.Failed("Paramètre 'action' requis : create_file, delete_cube, save, full_workflow");

        // Si aucun chemin fourni, utiliser le Bureau
        if (string.IsNullOrWhiteSpace(path))
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            path = Path.Combine(desktop, "sans_cube.blend");
        }

        return action.ToLowerInvariant() switch
        {
            "create_file" => await CreateFileAsync(type, cancellationToken),
            "delete_cube" => await DeleteCubeAsync(cancellationToken),
            "save" => await SaveAsync(path, cancellationToken),
            "full_workflow" => await FullWorkflowAsync(path, cancellationToken),
            _ => ToolResult.Failed($"Action inconnue : {action}. Utilise create_file, delete_cube, save, ou full_workflow.")
        };
    }

    private async Task<ToolResult> CreateFileAsync(string? type, CancellationToken ct)
    {
        var fileType = type?.ToLowerInvariant() switch
        {
            "video" => BlenderFile_type.VideoEditing,
            "vfx" => BlenderFile_type.VFX,
            "sculpt" => BlenderFile_type.Sculpting,
            _ => BlenderFile_type.General
        };

        var result = await _blender.CreateNewFileAsync(fileType, ct);
        return result.Success
            ? ToolResult.Succeeded($"Nouveau fichier Blender créé ({fileType}).")
            : ToolResult.Failed(result.ErrorMessage ?? "Échec création fichier.");
    }

    private async Task<ToolResult> DeleteCubeAsync(CancellationToken ct)
    {
        var result = await _blender.DeleteDefaultCubeAsync(ct);
        return result.Success
            ? ToolResult.Succeeded("Cube/lampe/caméra supprimés.")
            : ToolResult.Failed(result.ErrorMessage ?? "Échec suppression.");
    }

    private async Task<ToolResult> SaveAsync(string path, CancellationToken ct)
    {
        var result = await _blender.SaveFileAsync(path, ct);
        return result.Success
            ? ToolResult.Succeeded($"Fichier enregistré : {path}")
            : ToolResult.Failed(result.ErrorMessage ?? "Échec enregistrement.");
    }

    private async Task<ToolResult> FullWorkflowAsync(string path, CancellationToken ct)
    {
        _logger.LogInformation("[BlenderTool] Full workflow: {Path}", path);
        var result = await _blender.ExecuteFullWorkflowAsync(path, ct);
        return result.Success
            ? ToolResult.Succeeded($"Blender : nouveau fichier → cube supprimé → enregistré sous {path}")
            : ToolResult.Failed(result.ErrorMessage ?? "Échec workflow Blender.");
    }
}
