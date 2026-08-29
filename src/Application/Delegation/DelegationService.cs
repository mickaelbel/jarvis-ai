using System.Collections.Concurrent;
using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Delegation;

/// <summary>
/// Système de délégation : l'agent principal peut déléguer des sous-tâches
/// à des subagents isolés qui s'exécutent en parallèle. Chaque subagent a
/// son propre contexte et peut utiliser les mêmes outils que l'agent principal.
/// </summary>
public sealed class DelegationService
{
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolExecutor _toolExecutor;
    private readonly ILogger<DelegationService> _logger;
    private readonly ConcurrentDictionary<string, SubagentTask> _tasks = new();
    private int _maxConcurrent = 3;
    private int _activeCount;

    public int MaxConcurrent => _maxConcurrent;
    public int ActiveCount => _activeCount;

    public DelegationService(
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        ILogger<DelegationService> logger)
    {
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _logger = logger;
    }

    /// <summary>
    /// Démarre un subagent pour une tâche donnée. Retourne immédiatement
    /// un taskId que l'on peut poller pour le résultat.
    /// </summary>
    public async Task<string> DelegateAsync(string task, string? toolHint = null, CancellationToken cancellationToken = default)
    {
        var taskId = Guid.NewGuid().ToString("N")[..12];
        var sub = new SubagentTask
        {
            Id = taskId,
            Task = task,
            ToolHint = toolHint,
            Status = SubagentStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        _tasks[taskId] = sub;

        _logger.LogInformation("[Delegation] Tâche déléguée {TaskId}: {Task}", taskId, task);

        // Exécution asynchrone en arrière-plan
        _ = Task.Run(async () => await ExecuteSubagentAsync(sub, cancellationToken), cancellationToken);
        return taskId;
    }

    /// <summary>
    /// Démarre plusieurs subagents en parallèle (batch delegation).
    /// </summary>
    public async Task<IReadOnlyList<string>> DelegateBatchAsync(IReadOnlyList<string> tasks, CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        foreach (var task in tasks)
        {
            var id = await DelegateAsync(task, cancellationToken: cancellationToken);
            ids.Add(id);
        }
        return ids.AsReadOnly();
    }

    /// <summary>
    /// Récupère le résultat d'une tâche déléguée (bloquant si pas encore terminé).
    /// </summary>
    public async Task<SubagentResult> GetResultAsync(string taskId, CancellationToken cancellationToken = default)
    {
        while (_tasks.TryGetValue(taskId, out var task))
        {
            if (task.Status == SubagentStatus.Completed)
                return new SubagentResult(taskId, true, task.Result, task.Duration);
            if (task.Status == SubagentStatus.Failed)
                return new SubagentResult(taskId, false, task.ErrorMessage, task.Duration);

            await Task.Delay(200, cancellationToken);
        }
        return new SubagentResult(taskId, false, "Tâche introuvable", TimeSpan.Zero);
    }

    /// <summary>
    /// Récupère tous les résultats d'un batch.
    /// </summary>
    public async Task<IReadOnlyList<SubagentResult>> GetBatchResultsAsync(IReadOnlyList<string> taskIds, CancellationToken cancellationToken = default)
    {
        var results = new List<SubagentResult>();
        foreach (var id in taskIds)
            results.Add(await GetResultAsync(id, cancellationToken));
        return results.AsReadOnly();
    }

    /// <summary>
    /// Liste les tâches actives.
    /// </summary>
    public IReadOnlyList<SubagentTask> GetActiveTasks()
    {
        return _tasks.Values.Where(t => t.Status == SubagentStatus.Running || t.Status == SubagentStatus.Pending).ToList().AsReadOnly();
    }

    private async Task ExecuteSubagentAsync(SubagentTask sub, CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Interlocked.Increment(ref _activeCount);
            sub.Status = SubagentStatus.Running;

            // Si un toolHint est donné, exécuter directement cet outil
            if (!string.IsNullOrEmpty(sub.ToolHint))
            {
                var tool = _toolRegistry.GetByName(sub.ToolHint);
                if (tool is not null)
                {
                    var ctx = new AgentContext(sub.ToolHint, source: "subagent", new Dictionary<string, object> { ["task"] = sub.Task });
                    var parameters = new Dictionary<string, string> { ["task"] = sub.Task };
                    var result = await _toolExecutor.ExecuteAsync(sub.ToolHint, ctx, cancellationToken);
                    sub.Result = result.Output ?? result.ErrorMessage;
                    sub.Status = result.Success ? SubagentStatus.Completed : SubagentStatus.Failed;
                    if (!result.Success) sub.ErrorMessage = result.ErrorMessage;
                }
                else
                {
                    sub.ErrorMessage = $"Outil '{sub.ToolHint}' introuvable";
                    sub.Status = SubagentStatus.Failed;
                }
            }
            else
            {
                // Pas de tool hint — retourner la tâche comme "à traiter par l'agent principal"
                sub.Result = $"[Subagent {sub.Id}] Tâche déléguée: {sub.Task} — en attente de traitement";
                sub.Status = SubagentStatus.Completed;
            }

            sw.Stop();
            sub.Duration = sw.Elapsed;
            _logger.LogInformation("[Delegation] Tâche {TaskId} terminée ({Status}) en {Duration}ms",
                sub.Id, sub.Status, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            sub.Status = SubagentStatus.Failed;
            sub.ErrorMessage = ex.Message;
            sub.Duration = sw.Elapsed;
            _logger.LogError(ex, "[Delegation] Erreur subagent {TaskId}", sub.Id);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCount);
        }
    }
}

public sealed class SubagentTask
{
    public string Id { get; set; } = "";
    public string Task { get; set; } = "";
    public string? ToolHint { get; set; }
    public SubagentStatus Status { get; set; }
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public TimeSpan Duration { get; set; }
}

public enum SubagentStatus { Pending, Running, Completed, Failed }

public sealed record SubagentResult(string TaskId, bool Success, string? Output, TimeSpan Duration);
