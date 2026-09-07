using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Web.Services;

public sealed class GitAutoCommitTool : ToolBase
{
    private readonly IGitWorkflowService _git;

    public override string Name => "git_auto_commit";
    public override string Description => "Gère l'auto-commit git (config auto_commit.json). Actions: status, enable, disable, add_repo, remove_repo, run_now (exécute immédiatement un cycle).";
    public override string Category => "dev";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;
    public override TimeSpan Timeout => TimeSpan.FromMinutes(5);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "status, enable, disable, add_repo, remove_repo, run_now", typeof(string), required: true),
        new ToolParameter("repo_path", "Chemin du dépôt git (add_repo/remove_repo)", typeof(string)),
    };

    public GitAutoCommitTool(IGitWorkflowService git, ILogger<GitAutoCommitTool> logger) : base(logger)
    {
        _git = git;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var action = RequireParam(parameters, "action").ToLowerInvariant();
        switch (action)
        {
            case "status":
                return await BuildStatusAsync(cancellationToken);
            case "enable":
                return SetEnabled(true);
            case "disable":
                return SetEnabled(false);
            case "add_repo":
                return AddRepo(RequireParam(parameters, "repo_path"));
            case "remove_repo":
                return RemoveRepo(RequireParam(parameters, "repo_path"));
            case "run_now":
                return Ok(await GitAutoCommitRunner.RunCycleAsync(_git, Logger, cancellationToken));
            default:
                return Fail($"Action inconnue: {action}. Valides: status, enable, disable, add_repo, remove_repo, run_now");
        }
    }

    private async Task<ToolResult> BuildStatusAsync(CancellationToken cancellationToken)
    {
        var config = GitAutoCommitConfigStore.Load();
        var sb = new StringBuilder();
        sb.AppendLine(config.Enabled ? "Auto-commit : activé." : "Auto-commit : désactivé.");
        sb.AppendLine($"Intervalle : {config.IntervalMinutes} min.");
        sb.AppendLine($"Dernier intervalle exécuté : {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        if (config.Repos.Count == 0)
        {
            sb.AppendLine("Aucun dépôt configuré (voir auto_commit.json).");
            return Ok(sb.ToString());
        }

        sb.AppendLine("Dépôts surveillés :");
        foreach (var repo in config.Repos)
        {
            try
            {
                var status = await _git.GetStatusAsync(repo, cancellationToken);
                var state = status.IsClean
                    ? "arbre propre"
                    : $"{status.StagedFiles.Count + status.ModifiedFiles.Count + status.UntrackedFiles.Count} modification(s)";
                sb.AppendLine($"- {repo} (branche {status.Branch}, {state})");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"- {repo} (erreur : {ex.Message})");
            }
        }
        return Ok(sb.ToString());
    }

    private static ToolResult SetEnabled(bool enabled)
    {
        var config = GitAutoCommitConfigStore.Load();
        config.Enabled = enabled;
        GitAutoCommitConfigStore.Save(config);
        return Ok(enabled ? "Auto-commit activé." : "Auto-commit désactivé.");
    }

    private static ToolResult AddRepo(string repoPath)
    {
        if (!Directory.Exists(repoPath))
            return Fail($"Dépôt introuvable: {repoPath}");
        var config = GitAutoCommitConfigStore.Load();
        if (config.Repos.Any(r => r.Equals(repoPath, StringComparison.OrdinalIgnoreCase)))
            return Ok($"Dépôt déjà surveillé: {repoPath}");
        config.Repos.Add(repoPath);
        GitAutoCommitConfigStore.Save(config);
        return Ok($"Dépôt ajouté: {repoPath}");
    }

    private static ToolResult RemoveRepo(string repoPath)
    {
        var config = GitAutoCommitConfigStore.Load();
        if (config.Repos.RemoveAll(r => r.Equals(repoPath, StringComparison.OrdinalIgnoreCase)) == 0)
            return Fail($"Dépôt absent de la liste: {repoPath}");
        GitAutoCommitConfigStore.Save(config);
        return Ok($"Dépôt retiré: {repoPath}");
    }
}