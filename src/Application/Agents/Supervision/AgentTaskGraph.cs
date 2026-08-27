namespace JarvisAI.Application.Agents.Supervision;

/// <summary>
/// Graphe de tâches : résolveur de dépendances et de « prêt à exécuter ».
/// Gère les états avancés (dépendance échouée → tâches bloquées sautées/abandonnées).
/// </summary>
public sealed class AgentTaskGraph
{
    private readonly List<AgentTask> _tasks = new();
    private int _nextId;

    public AgentTask Add(string title, string goal, params int[] dependsOn)
    {
        var task = new AgentTask(title, goal)
        {
            Id = _nextId++,
            DependsOn = dependsOn ?? Array.Empty<int>()
        };
        _tasks.Add(task);
        return task;
    }

    public IReadOnlyList<AgentTask> Tasks => _tasks;

    public AgentTask? Get(int id) => _tasks.FirstOrDefault(t => t.Id == id);

    public bool HasRemaining => _tasks.Any(t => t.Status is AgentTaskStatus.Pending or AgentTaskStatus.Running);

    /// <summary>Les tâches Pending dont les dépendances sont toutes satisfaites.</summary>
    public IReadOnlyList<AgentTask> GetReady()
    {
        return _tasks
            .Where(t => t.Status == AgentTaskStatus.Pending && DependenciesSatisfied(t))
            .ToList();
    }

    public bool AllCompleted => _tasks.All(t => t.Status is AgentTaskStatus.Completed or AgentTaskStatus.Skipped or AgentTaskStatus.Aborted)
                                && !HasRemaining;

    /// <summary>Marque comme Aborted toutes les tâches dont une dépendance a échoué/été abandonnée.</summary>
    public void AbortBlocked()
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var task in _tasks.Where(t => t.Status == AgentTaskStatus.Pending))
            {
                if (task.DependsOn.Any(dep => IsTerminalFailure(dep)))
                {
                    task.MarkAborted();
                    changed = true;
                }
            }
        } while (changed);
    }

    private bool DependenciesSatisfied(AgentTask task)
    {
        if (task.DependsOn.Count == 0) return true;
        return task.DependsOn.All(dep =>
            IsSatisfied(dep) && !IsTerminalFailure(dep));
    }

    private bool IsSatisfied(int depId)
    {
        var dep = Get(depId);
        return dep is not null && dep.Status is AgentTaskStatus.Completed or AgentTaskStatus.Skipped;
    }

    private bool IsTerminalFailure(int depId)
    {
        var dep = Get(depId);
        return dep is not null && dep.Status is AgentTaskStatus.Failed or AgentTaskStatus.Aborted;
    }
}
