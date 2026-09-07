using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class SymlinkManagerTool : ToolBase
{
    private readonly ISymlinkManagerService _service;
    private readonly ILogger<SymlinkManagerTool> _logger;

    public override string Name => "symlink_manager";
    public override string Description => "Gérer les jonctions et liens symboliques Windows. Usage: symlink_manager(action: \"junction\", source: \"C:/lien\", target: \"C:/cible\"). Actions : junction, symlink, remove, list. Risque élevé : modifications du système de fichiers.";
    public override string Category => "system";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "junction, symlink, remove, list (requis)", typeof(string), required: true),
        new ToolParameter("source", "Chemin source du lien", typeof(string)),
        new ToolParameter("target", "Chemin cible", typeof(string)),
        new ToolParameter("is_directory", "true si c'est un lien de répertoire (pour symlink)", typeof(string))
    };

    public SymlinkManagerTool(ISymlinkManagerService service, ILogger<SymlinkManagerTool> logger)
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
        parameters.TryGetValue("source", out var source);
        parameters.TryGetValue("target", out var target);
        parameters.TryGetValue("is_directory", out var isDirectoryStr);

        var isDirectory = isDirectoryStr?.ToLowerInvariant() is "true";

        return action switch
        {
            "junction" => await JunctionAsync(source, target, ct),
            "symlink" => await SymlinkAsync(source, target, isDirectory, ct),
            "remove" => await RemoveAsync(source, ct),
            "list" => await ListAsync(source, ct),
            _ => Fail($"Action inconnue : '{action}'. Actions valides : junction, symlink, remove, list")
        };
    }

    private async Task<ToolResult> JunctionAsync(string? source, string? target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return Fail("Les paramètres 'source' et 'target' sont requis.");

        var result = await _service.CreateJunctionAsync(source, target, ct);
        if (!result.Success)
            return Fail($"Échec de la création de la jonction : {result.ErrorMessage}");

        return Ok($"Jonction créée : {source} → {target}");
    }

    private async Task<ToolResult> SymlinkAsync(string? source, string? target, bool isDirectory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return Fail("Les paramètres 'source' et 'target' sont requis.");

        var result = await _service.CreateSymlinkAsync(source, target, isDirectory, ct);
        if (!result.Success)
            return Fail($"Échec de la création du lien symbolique : {result.ErrorMessage}");

        return Ok($"Lien symbolique créé : {source} → {target}");
    }

    private async Task<ToolResult> RemoveAsync(string? source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
            return Fail("Le paramètre 'source' est requis.");

        var removed = await _service.RemoveJunctionAsync(source, ct);
        return removed
            ? Ok($"Lien supprimé : {source}")
            : Fail($"Échec de la suppression du lien : {source}");
    }

    private async Task<ToolResult> ListAsync(string? source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
            return Fail("Le paramètre 'source' (dossier racine) est requis.");

        var junctions = await _service.GetJunctionsAsync(source, ct);
        if (junctions.Count == 0)
            return Ok($"Aucune jonction/lien trouvé dans : {source}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Jonctions/liens dans {source} :");
        foreach (var j in junctions)
            sb.AppendLine($"  {j.Path} → {j.Target}");
        return Ok(sb.ToString());
    }
}
