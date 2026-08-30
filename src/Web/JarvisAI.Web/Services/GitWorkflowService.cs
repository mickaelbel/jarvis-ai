using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Web.Services;

public interface IGitWorkflowService
{
    Task<GitStatus> GetStatusAsync(string repoPath, CancellationToken ct = default);
    Task<string> CreateBranchAsync(string repoPath, string branchName, string? fromBranch = null, CancellationToken ct = default);
    Task<string> CommitAsync(string repoPath, string message, bool addAll = true, CancellationToken ct = default);
    Task<string> PushAsync(string repoPath, string? remote = null, string? branch = null, CancellationToken ct = default);
    Task<string> PullAsync(string repoPath, string? remote = null, string? branch = null, CancellationToken ct = default);
    Task<string> MergeAsync(string repoPath, string sourceBranch, CancellationToken ct = default);
    Task<string> CreateTagAsync(string repoPath, string tagName, string? message = null, CancellationToken ct = default);
    Task<string> StashAsync(string repoPath, string? message = null, CancellationToken ct = default);
    Task<string> RebaseAsync(string repoPath, string ontoBranch, CancellationToken ct = default);
    Task<IReadOnlyList<GitCommit>> GetLogAsync(string repoPath, int count = 10, CancellationToken ct = default);
}

public sealed class GitWorkflowService : IGitWorkflowService
{
    private readonly ILogger<GitWorkflowService> _logger;

    public GitWorkflowService(ILogger<GitWorkflowService> logger)
    {
        _logger = logger;
    }

    public async Task<GitStatus> GetStatusAsync(string repoPath, CancellationToken ct = default)
    {
        var output = await RunGitAsync(repoPath, "status --porcelain", ct);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var staged = new List<string>();
        var modified = new List<string>();
        var untracked = new List<string>();

        foreach (var line in lines)
        {
            var status = line[..2].Trim();
            var file = line[3..];

            if (status.Contains('A') || status.Contains('M') && line[0] != ' ')
                staged.Add(file);
            else if (status.Contains('M') || status.Contains('D'))
                modified.Add(file);
            else
                untracked.Add(file);
        }

        var branch = await RunGitAsync(repoPath, "branch --show-current", ct);

        return new GitStatus
        {
            Branch = branch.Trim(),
            StagedFiles = staged,
            ModifiedFiles = modified,
            UntrackedFiles = untracked,
            IsClean = lines.Length == 0
        };
    }

    public async Task<string> CreateBranchAsync(string repoPath, string branchName, string? fromBranch = null, CancellationToken ct = default)
    {
        var from = fromBranch ?? "main";
        return await RunGitAsync(repoPath, $"checkout -b {branchName} {from}", ct);
    }

    public async Task<string> CommitAsync(string repoPath, string message, bool addAll = true, CancellationToken ct = default)
    {
        if (addAll)
            await RunGitAsync(repoPath, "add -A", ct);

        return await RunGitAsync(repoPath, $"commit -m \"{message}\"", ct);
    }

    public async Task<string> PushAsync(string repoPath, string? remote = null, string? branch = null, CancellationToken ct = default)
    {
        var remoteName = remote ?? "origin";
        var branchName = branch ?? await GetBranchAsync(repoPath, ct);
        return await RunGitAsync(repoPath, $"push {remoteName} {branchName}", ct);
    }

    public async Task<string> PullAsync(string repoPath, string? remote = null, string? branch = null, CancellationToken ct = default)
    {
        var remoteName = remote ?? "origin";
        var branchName = branch ?? await GetBranchAsync(repoPath, ct);
        return await RunGitAsync(repoPath, $"pull {remoteName} {branchName}", ct);
    }

    public async Task<string> MergeAsync(string repoPath, string sourceBranch, CancellationToken ct = default)
    {
        return await RunGitAsync(repoPath, $"merge {sourceBranch}", ct);
    }

    public async Task<string> CreateTagAsync(string repoPath, string tagName, string? message = null, CancellationToken ct = default)
    {
        if (message is not null)
            return await RunGitAsync(repoPath, $"tag -a {tagName} -m \"{message}\"", ct);
        return await RunGitAsync(repoPath, $"tag {tagName}", ct);
    }

    public async Task<string> StashAsync(string repoPath, string? message = null, CancellationToken ct = default)
    {
        if (message is not null)
            return await RunGitAsync(repoPath, $"stash push -m \"{message}\"", ct);
        return await RunGitAsync(repoPath, "stash", ct);
    }

    public async Task<string> RebaseAsync(string repoPath, string ontoBranch, CancellationToken ct = default)
    {
        return await RunGitAsync(repoPath, $"rebase {ontoBranch}", ct);
    }

    public async Task<IReadOnlyList<GitCommit>> GetLogAsync(string repoPath, int count = 10, CancellationToken ct = default)
    {
        var output = await RunGitAsync(repoPath, $"log --oneline -{count}", ct);
        var commits = new List<GitCommit>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', 2);
            if (parts.Length == 2)
            {
                commits.Add(new GitCommit
                {
                    Hash = parts[0],
                    Message = parts[1]
                });
            }
        }

        return commits;
    }

    private async Task<string> GetBranchAsync(string repoPath, CancellationToken ct)
    {
        return (await RunGitAsync(repoPath, "branch --show-current", ct)).Trim();
    }

    private async Task<string> RunGitAsync(string repoPath, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return "Erreur: impossible de démarrer git";

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 && !string.IsNullOrEmpty(stderr))
                return $"Erreur: {stderr.Trim()}";

            return string.IsNullOrEmpty(stdout) ? "OK" : stdout.Trim();
        }
        catch (Exception ex)
        {
            return $"Git non disponible: {ex.Message}";
        }
    }
}

public sealed class GitStatus
{
    public string Branch { get; set; } = "";
    public List<string> StagedFiles { get; set; } = new();
    public List<string> ModifiedFiles { get; set; } = new();
    public List<string> UntrackedFiles { get; set; } = new();
    public bool IsClean { get; set; }
}

public sealed class GitCommit
{
    public string Hash { get; set; } = "";
    public string Message { get; set; } = "";
}
