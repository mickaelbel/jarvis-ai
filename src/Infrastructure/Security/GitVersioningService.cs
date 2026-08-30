using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Security;

public interface IGitVersioningService
{
    Task<bool> EnsureRepositoryAsync(string path, CancellationToken ct = default);
    Task<string> CommitAsync(string path, string message, CancellationToken ct = default);
    Task<IReadOnlyList<GitCommit>> GetHistoryAsync(string path, int maxCount = 20, CancellationToken ct = default);
    Task<string?> GetDiffAsync(string path, string? commitId = null, CancellationToken ct = default);
    Task<bool> RollbackAsync(string path, string commitId, CancellationToken ct = default);
    Task<IReadOnlyList<GitFileStatus>> GetStatusAsync(string path, CancellationToken ct = default);
}

public sealed class GitVersioningService : IGitVersioningService
{
    private readonly ILogger<GitVersioningService> _logger;

    public GitVersioningService(ILogger<GitVersioningService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> EnsureRepositoryAsync(string path, CancellationToken ct = default)
    {
        var gitDir = Path.Combine(path, ".git");
        if (Directory.Exists(gitDir))
            return true;

        _logger.LogInformation("[GitVersion] Initializing repo at {Path}", path);
        var result = await RunGitAsync(path, "init", ct);
        if (result.ExitCode != 0) return false;

        await RunGitAsync(path, "config user.email \"jarvis@local\"", ct);
        await RunGitAsync(path, "config user.name \"Jarvis AI\"", ct);

        var gitignore = Path.Combine(path, ".gitignore");
        if (!File.Exists(gitignore))
        {
            await File.WriteAllTextAsync(gitignore,
                "*.tmp\n*.log\nThumbs.db\n.DS_Store\nbin/\nobj/\n", ct);
        }

        return true;
    }

    public async Task<string> CommitAsync(string path, string message, CancellationToken ct = default)
    {
        await RunGitAsync(path, "add -A", ct);
        var result = await RunGitAsync(path, $"commit -m \"{message}\" --allow-empty", ct);

        if (result.ExitCode == 0)
        {
            _logger.LogInformation("[GitVersion] Committed: {Message}", message);
            return result.Output;
        }

        _logger.LogWarning("[GitVersion] Commit failed: {Error}", result.Error);
        return result.Error;
    }

    public async Task<IReadOnlyList<GitCommit>> GetHistoryAsync(string path, int maxCount = 20, CancellationToken ct = default)
    {
        var result = await RunGitAsync(path,
            $"log --oneline -{maxCount} --format=\"%H|%s|%ai\"", ct);

        if (result.ExitCode != 0) return new List<GitCommit>();

        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var parts = line.Split('|');
                return new GitCommit
                {
                    Id = parts.ElementAtOrDefault(0) ?? "",
                    Message = parts.ElementAtOrDefault(1) ?? "",
                    Date = DateTime.TryParse(parts.ElementAtOrDefault(2), out var d) ? d : DateTime.MinValue
                };
            })
            .ToList();
    }

    public async Task<string?> GetDiffAsync(string path, string? commitId = null, CancellationToken ct = default)
    {
        var args = string.IsNullOrEmpty(commitId) ? "diff" : $"diff {commitId} HEAD";
        var result = await RunGitAsync(path, args, ct);
        return result.ExitCode == 0 ? result.Output : null;
    }

    public async Task<bool> RollbackAsync(string path, string commitId, CancellationToken ct = default)
    {
        var result = await RunGitAsync(path, $"checkout {commitId} -- .", ct);
        if (result.ExitCode == 0)
        {
            _logger.LogInformation("[GitVersion] Rolled back to {CommitId}", commitId);
            return true;
        }
        return false;
    }

    public async Task<IReadOnlyList<GitFileStatus>> GetStatusAsync(string path, CancellationToken ct = default)
    {
        var result = await RunGitAsync(path, "status --porcelain", ct);
        if (result.ExitCode != 0) return new List<GitFileStatus>();

        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => new GitFileStatus
            {
                Status = line.Length >= 2 ? line[..2].Trim() : "?",
                FilePath = line.Length > 3 ? line[3..].Trim('"') : ""
            })
            .ToList();
    }

    private async Task<GitResult> RunGitAsync(string workingDir, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return new GitResult { ExitCode = process.ExitCode, Output = output.Trim(), Error = error.Trim() };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GitVersion] Git command failed: {Args}", arguments);
            return new GitResult { ExitCode = -1, Error = ex.Message };
        }
    }
}

internal sealed class GitResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class GitCommit
{
    public string Id { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime Date { get; set; }
}

public sealed class GitFileStatus
{
    public string Status { get; set; } = "";
    public string FilePath { get; set; } = "";
}
