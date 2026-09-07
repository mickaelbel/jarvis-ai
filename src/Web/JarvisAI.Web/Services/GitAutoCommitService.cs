using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public sealed class GitAutoCommitConfig
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 30;
    public List<string> Repos { get; set; } = new();
}

public static class GitAutoCommitConfigStore
{
    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "config", "auto_commit.json");

    public static GitAutoCommitConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<GitAutoCommitConfig>(File.ReadAllText(ConfigPath));
                if (loaded is not null)
                {
                    if (loaded.IntervalMinutes <= 0) loaded.IntervalMinutes = 30;
                    loaded.Repos ??= new List<string>();
                    return loaded;
                }
            }
        }
        catch
        {
        }
        return new GitAutoCommitConfig();
    }

    public static void Save(GitAutoCommitConfig config)
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public static class GitAutoCommitRunner
{
    public static async Task<string> RunCycleAsync(IGitWorkflowService git, ILogger logger, CancellationToken ct)
    {
        var config = GitAutoCommitConfigStore.Load();
        if (!config.Enabled)
            return "Auto-commit désactivé (voir auto_commit.json).";
        if (config.Repos.Count == 0)
            return "Aucun dépôt configuré (voir auto_commit.json).";

        var sb = new StringBuilder();
        foreach (var repo in config.Repos)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(repo) || !Directory.Exists(repo))
                {
                    sb.AppendLine($"- {repo}: dépôt introuvable.");
                    continue;
                }

                var status = await git.GetStatusAsync(repo, ct);
                if (status.IsClean)
                {
                    sb.AppendLine($"- {repo}: rien à committer.");
                    continue;
                }

                var changed = status.StagedFiles
                    .Concat(status.ModifiedFiles)
                    .Concat(status.UntrackedFiles)
                    .ToList();

                var first = changed.FirstOrDefault();
                var message = string.IsNullOrEmpty(first)
                    ? $"chore(auto): {status.Branch}"
                    : $"chore(auto): {first}" + (changed.Count > 1 ? $" et {changed.Count - 1} autres" : "");

                var output = await git.CommitAsync(repo, message, addAll: true, ct);
                logger.LogInformation("[GitAutoCommit] Commit '{Message}' dans {Repo} : {Output}", message, repo, output);
                sb.AppendLine($"- {repo}: commité '{message}'.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[GitAutoCommit] Échec du commit dans {Repo}", repo);
                sb.AppendLine($"- {repo}: échec ({ex.Message}).");
            }
        }
        return sb.ToString();
    }
}

public sealed class GitAutoCommitService : BackgroundService
{
    private readonly IGitWorkflowService _git;
    private readonly ILogger<GitAutoCommitService> _logger;
    private bool _noReposWarned;

    public GitAutoCommitService(IGitWorkflowService git, ILogger<GitAutoCommitService> logger)
    {
        _git = git;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = GitAutoCommitConfigStore.Load();
                if (config.Enabled && config.Repos.Count > 0)
                {
                    _noReposWarned = false;
                    await GitAutoCommitRunner.RunCycleAsync(_git, _logger, stoppingToken);
                }
                else if (!_noReposWarned)
                {
                    if (!config.Enabled)
                        _logger.LogInformation("[GitAutoCommit] Auto-commit désactivé (voir auto_commit.json)");
                    else
                        _logger.LogInformation("[GitAutoCommit] Aucun dépôt configuré (voir auto_commit.json)");
                    _noReposWarned = true;
                }

                var interval = config.IntervalMinutes > 0 ? config.IntervalMinutes : 30;
                await Task.Delay(TimeSpan.FromMinutes(interval), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GitAutoCommit] Erreur de boucle");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}