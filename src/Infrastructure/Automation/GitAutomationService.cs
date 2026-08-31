using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Automation;

public interface IGitAutomationService
{
    Task<GitResult> AutoCommitAsync(string repoPath, string? message = null, CancellationToken ct = default);
    Task<GitResult> GetStatusAsync(string repoPath, CancellationToken ct = default);
    Task<GitResult> CreateBranchAsync(string repoPath, string branchName, CancellationToken ct = default);
    Task<GitResult> SwitchBranchAsync(string repoPath, string branchName, CancellationToken ct = default);
    Task<GitResult> PullAsync(string repoPath, CancellationToken ct = default);
    Task<GitResult> PushAsync(string repoPath, CancellationToken ct = default);
    Task<List<GitCommit>> GetLogAsync(string repoPath, int limit = 20, CancellationToken ct = default);
}

public sealed class GitAutomationService : IGitAutomationService
{
    private readonly ILogger<GitAutomationService> _logger;

    public GitAutomationService(ILogger<GitAutomationService> logger)
    {
        _logger = logger;
    }

    public async Task<GitResult> AutoCommitAsync(string repoPath, string? message = null, CancellationToken ct = default)
    {
        var status = await RunGitAsync("status --porcelain", repoPath, ct);
        if (string.IsNullOrWhiteSpace(status.Output))
            return new GitResult { Success = true, Output = "Nothing to commit" };

        var addResult = await RunGitAsync("add -A", repoPath, ct);
        if (!addResult.Success) return addResult;

        var commitMsg = message ?? $"Auto-commit: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        var commitResult = await RunGitAsync($"commit -m \"{commitMsg}\"", repoPath, ct);

        _logger.LogInformation("[Git] Auto-committed: {Msg}", commitMsg);
        return commitResult;
    }

    public Task<GitResult> GetStatusAsync(string repoPath, CancellationToken ct = default)
        => RunGitAsync("status", repoPath, ct);

    public Task<GitResult> CreateBranchAsync(string repoPath, string branchName, CancellationToken ct = default)
        => RunGitAsync($"checkout -b {branchName}", repoPath, ct);

    public Task<GitResult> SwitchBranchAsync(string repoPath, string branchName, CancellationToken ct = default)
        => RunGitAsync($"checkout {branchName}", repoPath, ct);

    public Task<GitResult> PullAsync(string repoPath, CancellationToken ct = default)
        => RunGitAsync("pull", repoPath, ct);

    public Task<GitResult> PushAsync(string repoPath, CancellationToken ct = default)
        => RunGitAsync("push", repoPath, ct);

    public async Task<List<GitCommit>> GetLogAsync(string repoPath, int limit = 20, CancellationToken ct = default)
    {
        var result = await RunGitAsync($"log --oneline -{limit}", repoPath, ct);
        return result.Output?.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => new GitCommit
            {
                Hash = line.Split(' ')[0],
                Message = string.Join(" ", line.Split(' ').Skip(1))
            }).ToList() ?? new List<GitCommit>();
    }

    private async Task<GitResult> RunGitAsync(string arguments, string workingDir, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return new GitResult
            {
                Success = process.ExitCode == 0,
                Output = output,
                Error = error
            };
        }
        catch (Exception ex)
        {
            return new GitResult { Success = false, Error = ex.Message };
        }
    }
}

public sealed class GitResult
{
    public bool Success { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
}

public sealed class GitCommit
{
    public string Hash { get; set; } = "";
    public string Message { get; set; } = "";
}
