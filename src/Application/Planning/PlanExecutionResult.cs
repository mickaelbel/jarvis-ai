namespace JarvisAI.Application.Planning;

public sealed class PlanExecutionResult
{
    public bool Success { get; set; }
    public Guid PlanId { get; set; }
    public int StepsCompleted { get; set; }
    public int StepsTotal { get; set; }
    public string? Summary { get; set; }
    public string? ErrorMessage { get; set; }
    public TimeSpan TotalDuration { get; set; }

    public static PlanExecutionResult SuccessResult(Guid planId, int completed, int total, string? summary, TimeSpan duration)
        => new() { Success = true, PlanId = planId, StepsCompleted = completed, StepsTotal = total, Summary = summary, TotalDuration = duration };

    public static PlanExecutionResult FailureResult(Guid planId, int completed, int total, string? error, TimeSpan duration)
        => new() { Success = false, PlanId = planId, StepsCompleted = completed, StepsTotal = total, ErrorMessage = error, TotalDuration = duration };
}
