namespace JarvisAI.Application.Agents.Supervision;

public enum AgentTaskStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
    Aborted
}

/// <summary>
/// Une unité de travail dans un graphe de tâches. Peut dépendre d'autres tâches,
/// être retentée en cas d'échec, et marquée comme échec/abandonnée. Le résultat
/// d'une tâche peut alimenter les suivantes via la lecture de son <see cref="Result"/>.
/// </summary>
public sealed class AgentTask
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public IReadOnlyList<int> DependsOn { get; set; } = Array.Empty<int>();
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Pending;
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 2;
    public bool IsParallelizable { get; set; } = true;
    public string? Result { get; set; }
    public string? Error { get; set; }
    public TimeSpan Duration { get; set; }
    public string? Tab { get; set; }

    public AgentTask(string title, string goal)
    {
        Title = title;
        Goal = goal;
    }

    public bool CanRetry => RetryCount < MaxRetries;

    public void MarkRunning() => Status = AgentTaskStatus.Running;

    public void MarkCompleted(string? result = null)
    {
        Status = AgentTaskStatus.Completed;
        Result = result;
    }

    public void MarkFailed(string? error = null)
    {
        Status = AgentTaskStatus.Failed;
        Error = error;
    }

    public void MarkSkipped() => Status = AgentTaskStatus.Skipped;

    public void MarkAborted() => Status = AgentTaskStatus.Aborted;
}
