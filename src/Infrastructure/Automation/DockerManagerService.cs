using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Automation;

public interface IDockerManagerService
{
    Task<DockerResult> ListContainersAsync(bool all = true, CancellationToken ct = default);
    Task<DockerResult> StartContainerAsync(string containerId, CancellationToken ct = default);
    Task<DockerResult> StopContainerAsync(string containerId, CancellationToken ct = default);
    Task<DockerResult> RemoveContainerAsync(string containerId, CancellationToken ct = default);
    Task<DockerResult> ListImagesAsync(CancellationToken ct = default);
    Task<DockerResult> PullImageAsync(string imageName, CancellationToken ct = default);
}

public sealed class DockerManagerService : IDockerManagerService
{
    private readonly ILogger<DockerManagerService> _logger;

    public DockerManagerService(ILogger<DockerManagerService> logger)
    {
        _logger = logger;
    }

    public async Task<DockerResult> ListContainersAsync(bool all = true, CancellationToken ct = default)
    {
        var flag = all ? "-a" : "";
        return await RunDockerAsync($"ps {flag} --format \"table {{.ID}}\\t{{.Names}}\\t{{.Status}}\\t{{.Image}}\"", ct);
    }

    public Task<DockerResult> StartContainerAsync(string containerId, CancellationToken ct = default)
        => RunDockerAsync($"start {containerId}", ct);

    public Task<DockerResult> StopContainerAsync(string containerId, CancellationToken ct = default)
        => RunDockerAsync($"stop {containerId}", ct);

    public Task<DockerResult> RemoveContainerAsync(string containerId, CancellationToken ct = default)
        => RunDockerAsync($"rm -f {containerId}", ct);

    public Task<DockerResult> ListImagesAsync(CancellationToken ct = default)
        => RunDockerAsync("images --format \"table {{.Repository}}\\t{{.Tag}}\\t{{.Size}}\"", ct);

    public Task<DockerResult> PullImageAsync(string imageName, CancellationToken ct = default)
        => RunDockerAsync($"pull {imageName}", ct);

    private async Task<DockerResult> RunDockerAsync(string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return new DockerResult
            {
                Success = process.ExitCode == 0,
                Output = output,
                Error = error
            };
        }
        catch (Exception ex)
        {
            return new DockerResult { Success = false, Error = ex.Message };
        }
    }
}

public sealed class DockerResult
{
    public bool Success { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
}
