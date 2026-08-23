namespace JarvisAI.Application.Planning;

public sealed class PlanStep
{
    public int Index { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? ToolName { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
    public string Description { get; set; } = string.Empty;
    public PlanStepStatus Status { get; set; } = PlanStepStatus.Pending;
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
    public TimeSpan Duration { get; set; }
}

public enum PlanStepStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Skipped
}
