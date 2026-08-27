using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Agents.Supervision;

/// <summary>Delegate exécutant une tâche. Retourne (success, result, error).</summary>
public delegate Task<(bool Success, string? Result, string? Error)> AgentTaskExecutor(AgentTask task, CancellationToken cancellationToken);

/// <summary>
/// Exécuteur du graphe de tâches : boucle « récupérer les tâches prêtes → les
/// exécuter (en parallèle quand autorisé) → retenter les échecs récupérables →
/// abandonner les tâches bloquées par une dépendance échouée ». Cette boucle est
/// la colonne vertébrale « action → observation → raisonnement → prochaine action »
/// du superviseur, avec reprise après erreur au niveau de la tâche.
/// </summary>
public sealed class TaskExecutor
{
    private readonly ILogger<TaskExecutor> _logger;

    public TaskExecutor(ILogger<TaskExecutor>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskExecutor>.Instance;
    }

    /// <summary>
    /// Exécute le graphe jusqu'à épuisement (tout complété/échec/abandonné).
    /// </summary>
    public async Task RunAsync(
        AgentTaskGraph graph,
        AgentTaskExecutor executor,
        bool parallelize = true,
        Action<AgentTask, AgentTaskStatus>? onStatus = null,
        CancellationToken cancellationToken = default)
    {
        if (graph is null) throw new ArgumentNullException(nameof(graph));
        if (executor is null) throw new ArgumentNullException(nameof(executor));

        while (graph.HasRemaining)
        {
            cancellationToken.ThrowIfCancellationRequested();

            graph.AbortBlocked();
            var ready = graph.GetReady();
            if (ready.Count == 0)
            {
                // Plus rien d'exécutable et pas encore terminé → tout est bloqué.
                break;
            }

            var batch = parallelize ? ready : ready.Take(1).ToList();
            foreach (var task in batch)
                task.MarkRunning();

            if (batch.Count == 1)
            {
                await ExecuteWithRetryAsync(graph, batch[0], executor, onStatus, cancellationToken);
            }
            else
            {
                await Task.WhenAll(batch.Select(t => ExecuteWithRetryAsync(graph, t, executor, onStatus, cancellationToken)));
            }
        }
    }

    private async Task ExecuteWithRetryAsync(
        AgentTaskGraph graph,
        AgentTask task,
        AgentTaskExecutor executor,
        Action<AgentTask, AgentTaskStatus>? onStatus,
        CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        // MaxRetries = nombre de nouvelles tentatives APRÈS le premier essai.
        var maxAttempts = Math.Max(1, task.MaxRetries + 1);
        var attempt = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                attempt++;
                var (success, result, error) = await executor(task, ct);
                task.Duration = DateTime.UtcNow - started;

                if (success)
                {
                    task.MarkCompleted(result);
                    onStatus?.Invoke(task, AgentTaskStatus.Completed);
                    return;
                }

                task.RetryCount++;
                if (attempt >= maxAttempts)
                {
                    task.MarkFailed(error ?? "Tâche échouée après retries épuisés");
                    onStatus?.Invoke(task, AgentTaskStatus.Failed);
                    return;
                }

                _logger.LogWarning("[TaskExecutor] Tâche {Id} échouée (retry {Retry}/{Max}) : {Error}",
                    task.Id, attempt, task.MaxRetries, error);
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
        }
        catch (OperationCanceledException)
        {
            task.MarkAborted();
            onStatus?.Invoke(task, AgentTaskStatus.Aborted);
        }
        catch (Exception ex)
        {
            task.MarkFailed(ex.Message);
            onStatus?.Invoke(task, AgentTaskStatus.Failed);
        }
    }
}
