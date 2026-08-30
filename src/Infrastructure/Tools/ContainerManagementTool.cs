using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class ContainerManagementTool : ITool
{
    private readonly ILogger<ContainerManagementTool> _logger;

    public string Name => "docker";
    public string Description => "Gérer les containers Docker : lister, démarrer, arrêter, inspecter, logs, exécuter commandes";
    public string Category => "DevOps";
    public bool IsReadOnly => false;

    public ContainerManagementTool(ILogger<ContainerManagementTool> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "list, start, stop, restart, remove, logs, exec, ps, images, inspect", typeof(string), required: true),
        new ToolParameter("container", "Nom ou ID du container", typeof(string)),
        new ToolParameter("command", "Commande à exécuter (pour exec)", typeof(string)),
        new ToolParameter("image", "Nom de l'image (pour pull/run)", typeof(string)),
        new ToolParameter("tail", "Nombre de lignes de logs (défaut: 100)", typeof(string))
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        var action = parameters.GetValueOrDefault("action") ?? "ps";

        try
        {
            return action switch
            {
                "ps" => await ListContainers(parameters),
                "images" => await ListImages(),
                "logs" => await GetLogs(parameters),
                "inspect" => await InspectContainer(parameters),
                "start" => await StartContainer(parameters),
                "stop" => await StopContainer(parameters),
                "restart" => await RestartContainer(parameters),
                "remove" => await RemoveContainer(parameters),
                "exec" => await ExecCommand(parameters),
                _ => ToolResult.Failed($"Action inconnue: {action}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Docker] Error");
            return ToolResult.Failed($"Erreur Docker: {ex.Message}");
        }
    }

    private async Task<ToolResult> ListContainers(IReadOnlyDictionary<string, string> parameters)
    {
        var output = await RunDockerCommand("ps -a --format \"table {{.ID}}\t{{.Names}}\t{{.Status}}\t{{.Image}}\t{{.Ports}}\"");
        return ToolResult.Succeeded(output);
    }

    private async Task<ToolResult> ListImages()
    {
        var output = await RunDockerCommand("images --format \"table {{.Repository}}\t{{.Tag}}\t{{.Size}}\t{{.CreatedSince}}\"");
        return ToolResult.Succeeded(output);
    }

    private async Task<ToolResult> GetLogs(IReadOnlyDictionary<string, string> parameters)
    {
        var container = parameters.GetValueOrDefault("container");
        if (string.IsNullOrEmpty(container))
            return ToolResult.Failed("Container name/ID requis");

        var tail = parameters.GetValueOrDefault("tail") ?? "100";
        var output = await RunDockerCommand($"logs --tail {tail} {container}");
        return ToolResult.Succeeded($"Logs de {container}:\n{output}");
    }

    private async Task<ToolResult> InspectContainer(IReadOnlyDictionary<string, string> parameters)
    {
        var container = parameters.GetValueOrDefault("container");
        if (string.IsNullOrEmpty(container))
            return ToolResult.Failed("Container name/ID requis");

        var output = await RunDockerCommand($"inspect --format '{{{{.State.Status}}}} | Created: {{{{.Created}}}} | Image: {{{{.Config.Image}}}}' {container}");
        return ToolResult.Succeeded($"Inspect {container}:\n{output}");
    }

    private async Task<ToolResult> StartContainer(IReadOnlyDictionary<string, string> parameters)
    {
        var container = parameters.GetValueOrDefault("container");
        if (string.IsNullOrEmpty(container))
            return ToolResult.Failed("Container name/ID requis");

        var output = await RunDockerCommand($"start {container}");
        return ToolResult.Succeeded($"Container {container} démarré");
    }

    private async Task<ToolResult> StopContainer(IReadOnlyDictionary<string, string> parameters)
    {
        var container = parameters.GetValueOrDefault("container");
        if (string.IsNullOrEmpty(container))
            return ToolResult.Failed("Container name/ID requis");

        var output = await RunDockerCommand($"stop {container}");
        return ToolResult.Succeeded($"Container {container} arrêté");
    }

    private async Task<ToolResult> RestartContainer(IReadOnlyDictionary<string, string> parameters)
    {
        var container = parameters.GetValueOrDefault("container");
        if (string.IsNullOrEmpty(container))
            return ToolResult.Failed("Container name/ID requis");

        var output = await RunDockerCommand($"restart {container}");
        return ToolResult.Succeeded($"Container {container} redémarré");
    }

    private async Task<ToolResult> RemoveContainer(IReadOnlyDictionary<string, string> parameters)
    {
        var container = parameters.GetValueOrDefault("container");
        if (string.IsNullOrEmpty(container))
            return ToolResult.Failed("Container name/ID requis");

        var output = await RunDockerCommand($"rm -f {container}");
        return ToolResult.Succeeded($"Container {container} supprimé");
    }

    private async Task<ToolResult> ExecCommand(IReadOnlyDictionary<string, string> parameters)
    {
        var container = parameters.GetValueOrDefault("container");
        var command = parameters.GetValueOrDefault("command");

        if (string.IsNullOrEmpty(container) || string.IsNullOrEmpty(command))
            return ToolResult.Failed("Container et commande requis");

        var output = await RunDockerCommand($"exec {container} {command}");
        return ToolResult.Succeeded(output);
    }

    private async Task<string> RunDockerCommand(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return "Erreur: impossible de démarrer docker";

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 && !string.IsNullOrEmpty(stderr))
                return $"Erreur: {stderr.Trim()}";

            return string.IsNullOrEmpty(stdout) ? "OK" : stdout.Trim();
        }
        catch (Exception ex)
        {
            return $"Docker non disponible: {ex.Message}";
        }
    }
}
