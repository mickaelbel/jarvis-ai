using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Automation;

public interface IDockerManagerService
{
    Task<DockerResult> ListContainersAsync(bool all = false, CancellationToken ct = default);
    Task<DockerResult> StartContainerAsync(string containerId, CancellationToken ct = default);
    Task<DockerResult> StopContainerAsync(string containerId, CancellationToken ct = default);
    Task<DockerResult> RemoveContainerAsync(string containerId, bool force = false, CancellationToken ct = default);
    Task<DockerResult> ListImagesAsync(CancellationToken ct = default);
    Task<DockerResult> ContainerLogsAsync(string containerId, int tail = 100, CancellationToken ct = default);
}

public sealed class DockerManagerService : IDockerManagerService
{
    private readonly ILogger<DockerManagerService> _logger;
    private bool _dockerChecked;
    private bool _dockerAvailable;

    public DockerManagerService(ILogger<DockerManagerService> logger)
    {
        _logger = logger;
    }

    public async Task<DockerResult> ListContainersAsync(bool all = false, CancellationToken ct = default)
    {
        var args = all ? "ps --all --format \"{{.ID}}|{{.Names}}|{{.Image}}|{{.Status}}|{{.Ports}}\"" : "ps --format \"{{.ID}}|{{.Names}}|{{.Image}}|{{.Status}}|{{.Ports}}\"";
        var result = await RunDockerAsync(args, ct);

        if (!result.Success) return result;

        result.Containers = ParseContainerLines(result.RawOutput);

        var running = result.Containers.Count(c => c.Status.StartsWith("Up", StringComparison.OrdinalIgnoreCase));
        var stopped = result.Containers.Count - running;
        result.Summary = $"Conteneurs : {result.Containers.Count} | en exécution : {running}, arrêtés : {stopped}";

        return result;
    }

    public async Task<DockerResult> StartContainerAsync(string containerId, CancellationToken ct = default)
    {
        var result = await RunDockerAsync($"start \"{containerId}\"", ct);
        if (result.Success)
            result.Summary = $"Conteneur {containerId} démarré avec succès";
        return result;
    }

    public async Task<DockerResult> StopContainerAsync(string containerId, CancellationToken ct = default)
    {
        var result = await RunDockerAsync($"stop \"{containerId}\"", ct);
        if (result.Success)
            result.Summary = $"Conteneur {containerId} arrêté avec succès";
        return result;
    }

    public async Task<DockerResult> RemoveContainerAsync(string containerId, bool force = false, CancellationToken ct = default)
    {
        var args = force ? $"rm -f \"{containerId}\"" : $"rm \"{containerId}\"";
        var result = await RunDockerAsync(args, ct);
        if (result.Success)
            result.Summary = $"Conteneur {containerId} supprimé avec succès";
        return result;
    }

    public async Task<DockerResult> ListImagesAsync(CancellationToken ct = default)
    {
        var args = "images --format \"{{.Repository}}|{{.Tag}}|{{.ID}}|{{.Size}}|{{.CreatedSince}}\"";
        var result = await RunDockerAsync(args, ct);

        if (!result.Success) return result;

        result.Images = ParseImageLines(result.RawOutput);
        result.Summary = $"Images : {result.Images.Count}";

        return result;
    }

    public async Task<DockerResult> ContainerLogsAsync(string containerId, int tail = 100, CancellationToken ct = default)
    {
        var result = await RunDockerAsync($"logs --tail {tail} \"{containerId}\"", ct);
        if (result.Success)
            result.Summary = $"Journaux du conteneur {containerId} ({tail} dernières lignes)";
        return result;
    }

    private async Task<DockerResult> RunDockerAsync(string args, CancellationToken ct)
    {
        var result = new DockerResult();

        try
        {
            if (!_dockerChecked)
            {
                var versionResult = await RunProcessAsync("docker", "--version", ct);
                _dockerAvailable = versionResult.ExitCode == 0;
                _dockerChecked = true;

                if (!_dockerAvailable)
                {
                    result.ErrorMessage = "Docker CLI introuvable (installe Docker Desktop)";
                    return result;
                }
            }

            if (!_dockerAvailable)
            {
                result.ErrorMessage = "Docker CLI introuvable (installe Docker Desktop)";
                return result;
            }

            var processResult = await RunProcessAsync("docker", args, ct);
            result.RawOutput = processResult.Output;
            result.Success = processResult.ExitCode == 0;

            if (!result.Success)
                result.ErrorMessage = string.IsNullOrWhiteSpace(processResult.Error) ? "Commande Docker échouée" : processResult.Error;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Docker] Erreur exécution commande");
        }

        return result;
    }

    private static async Task<(string Output, string Error, int ExitCode)> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        if (process is null)
            return ("", "Impossible de démarrer le processus", -1);

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (output, error, process.ExitCode);
    }

    private static List<DockerContainer> ParseContainerLines(string raw)
    {
        var containers = new List<DockerContainer>();
        if (string.IsNullOrWhiteSpace(raw)) return containers;

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 5) continue;

            containers.Add(new DockerContainer
            {
                Id = parts[0].Trim(),
                Name = parts[1].Trim(),
                Image = parts[2].Trim(),
                Status = parts[3].Trim(),
                Ports = parts[4].Trim()
            });
        }

        return containers;
    }

    private static List<DockerImage> ParseImageLines(string raw)
    {
        var images = new List<DockerImage>();
        if (string.IsNullOrWhiteSpace(raw)) return images;

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 5) continue;

            images.Add(new DockerImage
            {
                Repository = parts[0].Trim(),
                Tag = parts[1].Trim(),
                Id = parts[2].Trim(),
                Size = parts[3].Trim(),
                CreatedSince = parts[4].Trim()
            });
        }

        return images;
    }
}

public sealed class DockerResult
{
    public bool Success { get; set; }
    public string RawOutput { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<DockerContainer> Containers { get; set; } = new();
    public List<DockerImage> Images { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public sealed class DockerContainer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public string Status { get; set; } = "";
    public string Ports { get; set; } = "";
}

public sealed class DockerImage
{
    public string Repository { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Id { get; set; } = "";
    public string Size { get; set; } = "";
    public string CreatedSince { get; set; } = "";
}
