namespace JarvisAI.Application.Planning;

public sealed class Plan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Goal { get; set; } = string.Empty;
    public List<PlanStep> Steps { get; set; } = new();
    public PlanStatus Status { get; set; } = PlanStatus.Created;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? Summary { get; set; }
    public Guid CorrelationId { get; set; } = Guid.NewGuid();

    public PlanStep? CurrentStep => Steps.FirstOrDefault(s => s.Status == PlanStepStatus.InProgress || s.Status == PlanStepStatus.Pending);

    public int CurrentStepIndex => Steps.FindIndex(s => s.Status == PlanStepStatus.InProgress || s.Status == PlanStepStatus.Pending);

    public bool AllStepsCompleted => Steps.All(s => s.Status == PlanStepStatus.Completed || s.Status == PlanStepStatus.Skipped);

    public bool HasFailedStep => Steps.Any(s => s.Status == PlanStepStatus.Failed);

    public void MarkInProgress()
    {
        Status = PlanStatus.InProgress;
    }

    public void MarkCompleted(string? summary = null)
    {
        Status = PlanStatus.Completed;
        CompletedAt = DateTime.UtcNow;
        Summary = summary;
    }

    public void MarkFailed(string? errorMessage = null)
    {
        Status = PlanStatus.Failed;
        CompletedAt = DateTime.UtcNow;
        Summary = errorMessage;
    }
}
