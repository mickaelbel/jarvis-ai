using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class DockerManagerTool : ToolBase
{
    private readonly IDockerManagerService _service;

    public override string Name => "docker_manager";
    public override string Description => "Gère les conteneurs et images Docker. Usage: docker_manager(action: \"list\"), docker_manager(action: \"start\", container_id: \"abc\"), docker_manager(action: \"stop\"), docker_manager(action: \"remove\", container_id: \"abc\", force: \"true\"), docker_manager(action: \"images\"), docker_manager(action: \"logs\", container_id: \"abc\", tail: \"100\").";
    public override string Category => "system";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "list, start, stop, remove, images, logs", typeof(string), required: true),
        new ToolParameter("container_id", "Identifiant ou nom du conteneur (start, stop, remove, logs)", typeof(string)),
        new ToolParameter("force", "remove: suppression forcée -f ; list: inclure tous les conteneurs (true/false, défaut false)", typeof(string)),
        new ToolParameter("tail", "Nombre de dernières lignes de logs (défaut 100)", typeof(string)),
    };

    public DockerManagerTool(IDockerManagerService service, ILogger<DockerManagerTool> logger)
        : base(logger)
    {
        _service = service;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var action = RequireParam(parameters, "action").ToLowerInvariant();
        parameters.TryGetValue("container_id", out var containerId);
        parameters.TryGetValue("force", out var forceStr);
        parameters.TryGetValue("tail", out var tailStr);

        var force = ParseBool(forceStr);
        var tail = int.TryParse(tailStr, out var parsedTail) && parsedTail > 0 ? parsedTail : 100;

        return action switch
        {
            "list" => await ListAsync(force, ct),
            "images" => await ImagesAsync(ct),
            "start" => await StartAsync(containerId, ct),
            "stop" => await StopAsync(containerId, ct),
            "remove" => await RemoveAsync(containerId, force, ct),
            "logs" => await LogsAsync(containerId, tail, ct),
            _ => Fail($"Action inconnue : '{action}'. Actions valides : list, start, stop, remove, images, logs")
        };
    }

    private async Task<ToolResult> ListAsync(bool all, CancellationToken ct)
    {
        var result = await _service.ListContainersAsync(all, ct);
        if (!result.Success)
            return Fail($"Erreur Docker : {result.ErrorMessage}");

        var sb = new StringBuilder();
        sb.AppendLine(result.Summary);
        if (result.Containers.Count == 0)
        {
            sb.AppendLine(all ? "Aucun conteneur (y compris arrêtés)." : "Aucun conteneur en exécution (force: \"true\" pour inclure les arrêtés).");
            return Ok(sb.ToString());
        }

        foreach (var container in result.Containers)
            sb.AppendLine($"  {container.Id} | {container.Name} | {container.Image} | {container.Status} | {container.Ports}");
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> ImagesAsync(CancellationToken ct)
    {
        var result = await _service.ListImagesAsync(ct);
        if (!result.Success)
            return Fail($"Erreur Docker : {result.ErrorMessage}");

        var sb = new StringBuilder();
        sb.AppendLine(result.Summary);
        if (result.Images.Count == 0)
        {
            sb.AppendLine("Aucune image Docker trouvée.");
            return Ok(sb.ToString());
        }

        foreach (var image in result.Images)
            sb.AppendLine($"  {image.Repository}:{image.Tag} | {image.Id} | {image.Size} | {image.CreatedSince}");
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> StartAsync(string? containerId, CancellationToken ct)
    {
        var id = RequireContainer(containerId);
        var result = await _service.StartContainerAsync(id, ct);
        return FormatActionResult(result);
    }

    private async Task<ToolResult> StopAsync(string? containerId, CancellationToken ct)
    {
        var id = RequireContainer(containerId);
        var result = await _service.StopContainerAsync(id, ct);
        return FormatActionResult(result);
    }

    private async Task<ToolResult> RemoveAsync(string? containerId, bool force, CancellationToken ct)
    {
        var id = RequireContainer(containerId);
        var result = await _service.RemoveContainerAsync(id, force, ct);
        return FormatActionResult(result);
    }

    private async Task<ToolResult> LogsAsync(string? containerId, int tail, CancellationToken ct)
    {
        var id = RequireContainer(containerId);
        var result = await _service.ContainerLogsAsync(id, tail, ct);
        if (!result.Success)
            return Fail($"Erreur Docker : {result.ErrorMessage}");

        var sb = new StringBuilder();
        sb.AppendLine(result.Summary);
        if (string.IsNullOrWhiteSpace(result.RawOutput))
            sb.AppendLine("Aucun journal.");
        else
            sb.AppendLine(result.RawOutput.TrimEnd());
        return Ok(sb.ToString());
    }

    private ToolResult FormatActionResult(DockerResult result)
    {
        if (!result.Success)
            return Fail($"Erreur Docker : {result.ErrorMessage}");
        if (!string.IsNullOrWhiteSpace(result.Summary))
            return Ok(result.Summary);
        return Ok(string.IsNullOrWhiteSpace(result.RawOutput) ? "Commande Docker exécutée avec succès." : result.RawOutput.TrimEnd());
    }

    private static string RequireContainer(string? containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId))
            throw new ArgumentException("Paramètre requis manquant : 'container_id'.");
        return containerId;
    }

    private static bool ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.ToLowerInvariant() is "true" or "1" or "oui" or "yes";
    }
}