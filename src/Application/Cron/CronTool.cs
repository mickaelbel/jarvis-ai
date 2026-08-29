using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Cron;

/// <summary>
/// Outil cron : l'agent peut créer, lister, supprimer des jobs planifiés.
/// </summary>
public sealed class CronTool : ITool
{
    private readonly CronScheduler _scheduler;
    private readonly ILogger<CronTool> _logger;

    public string Name => "cronjob";
    public string Description =>
        "Gérer les automatisations planifiées (cron). Créer, lister, activer/désactiver " +
        "des tâches récurrentes. Exemples: 'tous les jours à 9h', 'chaque lundi', 'dans 30min'.";
    public string Category => "automation";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Action: create, list, remove, enable, disable", typeof(string), required: true),
        new ToolParameter("name", "Nom du job (pour create)", typeof(string), required: false),
        new ToolParameter("schedule", "Expression cron ou naturelle: '30m', '1h', '0 9 * * *', 'every monday 9am'", typeof(string), required: false),
        new ToolParameter("task", "Description de la tâche à exécuter", typeof(string), required: false),
        new ToolParameter("job_id", "ID du job (pour remove/enable/disable)", typeof(string), required: false),
    };

    public CronTool(CronScheduler scheduler, ILogger<CronTool> logger)
    {
        _scheduler = scheduler;
        _logger = logger;
    }

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        if (string.IsNullOrWhiteSpace(action))
            return Task.FromResult(ToolResult.Failed("Paramètre 'action' requis: create, list, remove, enable, disable"));

        return action.ToLowerInvariant() switch
        {
            "create" => CreateJob(parameters),
            "list" => ListJobs(),
            "remove" => RemoveJob(parameters),
            "enable" => EnableJob(parameters),
            "disable" => DisableJob(parameters),
            _ => Task.FromResult(ToolResult.Failed($"Action inconnue: {action}. Utilise create, list, remove, enable, disable."))
        };
    }

    private Task<ToolResult> CreateJob(IReadOnlyDictionary<string, string> p)
    {
        p.TryGetValue("name", out var name);
        p.TryGetValue("schedule", out var schedule);
        p.TryGetValue("task", out var task);

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(schedule) || string.IsNullOrWhiteSpace(task))
            return Task.FromResult(ToolResult.Failed("Pour create: name, schedule, task sont requis."));

        var job = _scheduler.AddJob(name, schedule, task);
        return Task.FromResult(ToolResult.Succeeded($"Job '{name}' créé ({job.Id}). Prochain run: {job.NextRun:yyyy-MM-dd HH:mm} UTC"));
    }

    private Task<ToolResult> ListJobs()
    {
        var jobs = _scheduler.GetAllJobs();
        if (jobs.Count == 0)
            return Task.FromResult(ToolResult.Succeeded("Aucun job planifié."));

        var lines = jobs.Select(j => $"- [{(j.Enabled ? "ON" : "OFF")}] {j.Name} ({j.Id}): {j.CronExpression} -> prochain: {j.NextRun:yyyy-MM-dd HH:mm} UTC, exécuté {j.RunCount}x");
        return Task.FromResult(ToolResult.Succeeded(string.Join("\n", lines)));
    }

    private Task<ToolResult> RemoveJob(IReadOnlyDictionary<string, string> p)
    {
        p.TryGetValue("job_id", out var jobId);
        if (string.IsNullOrWhiteSpace(jobId))
            return Task.FromResult(ToolResult.Failed("job_id requis pour remove."));
        var removed = _scheduler.RemoveJob(jobId);
        return Task.FromResult(removed ? ToolResult.Succeeded("Job supprimé.") : ToolResult.Failed("Job introuvable."));
    }

    private Task<ToolResult> EnableJob(IReadOnlyDictionary<string, string> p)
    {
        p.TryGetValue("job_id", out var jobId);
        if (string.IsNullOrWhiteSpace(jobId)) return Task.FromResult(ToolResult.Failed("job_id requis."));
        _scheduler.EnableJob(jobId);
        return Task.FromResult(ToolResult.Succeeded("Job activé."));
    }

    private Task<ToolResult> DisableJob(IReadOnlyDictionary<string, string> p)
    {
        p.TryGetValue("job_id", out var jobId);
        if (string.IsNullOrWhiteSpace(jobId)) return Task.FromResult(ToolResult.Failed("job_id requis."));
        _scheduler.DisableJob(jobId);
        return Task.FromResult(ToolResult.Succeeded("Job désactivé."));
    }
}
