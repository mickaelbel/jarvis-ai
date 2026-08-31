using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Automation;

public interface ISymlinkManagerService
{
    Task<SymlinkResult> CreateJunctionAsync(string source, string target, CancellationToken ct = default);
    Task<SymlinkResult> CreateSymlinkAsync(string source, string target, bool isDirectory = false, CancellationToken ct = default);
    Task<bool> RemoveJunctionAsync(string path, CancellationToken ct = default);
    Task<List<JunctionInfo>> GetJunctionsAsync(string rootPath, CancellationToken ct = default);
}

public sealed class SymlinkManagerService : ISymlinkManagerService
{
    private readonly ILogger<SymlinkManagerService> _logger;

    public SymlinkManagerService(ILogger<SymlinkManagerService> logger)
    {
        _logger = logger;
    }

    public async Task<SymlinkResult> CreateJunctionAsync(string source, string target, CancellationToken ct = default)
    {
        var result = new SymlinkResult { Source = source, Target = target };

        try
        {
            if (Directory.Exists(source))
                Directory.Delete(source);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{source}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            await process!.WaitForExitAsync(ct);

            result.Success = process.ExitCode == 0;
            if (!result.Success)
                result.ErrorMessage = "mklink /J failed";

            _logger.LogInformation("[Symlink] Created junction: {Source} → {Target}", source, target);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Symlink] Failed: {Source}", source);
        }

        return result;
    }

    public async Task<SymlinkResult> CreateSymlinkAsync(string source, string target, bool isDirectory = false, CancellationToken ct = default)
    {
        var result = new SymlinkResult { Source = source, Target = target };

        try
        {
            var flag = isDirectory ? "/D" : "";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink {flag} \"{source}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            await process!.WaitForExitAsync(ct);

            result.Success = process.ExitCode == 0;
            _logger.LogInformation("[Symlink] Created symlink: {Source} → {Target}", source, target);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Symlink] Failed: {Source}", source);
        }

        return result;
    }

    public async Task<bool> RemoveJunctionAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c rmdir \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            await process!.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    public Task<List<JunctionInfo>> GetJunctionsAsync(string rootPath, CancellationToken ct = default)
    {
        var junctions = new List<JunctionInfo>();

        try
        {
            foreach (var dir in Directory.GetDirectories(rootPath))
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    junctions.Add(new JunctionInfo
                    {
                        Path = dir,
                        Target = info.FullName
                    });
                }
            }
        }
        catch { }

        return Task.FromResult(junctions);
    }
}

public sealed class SymlinkResult
{
    public bool Success { get; set; }
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string? ErrorMessage { get; set; }
}

public sealed class JunctionInfo
{
    public string Path { get; set; } = "";
    public string Target { get; set; } = "";
}
