namespace JarvisAI.Application.Agents;

public sealed record OrchestrationResult
{
    public Guid RunId { get; init; }
    public bool Success { get; init; }
    public string FinalResponse { get; init; } = string.Empty;
    public int Iterations { get; init; }
    public int Corrections { get; init; }
    public int Retries { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Reason { get; init; }
    public RunStatus Status { get; init; }
    public RunRecord? Run { get; init; }

    public static OrchestrationResult FromRun(RunRecord run, string finalResponse, int iterations, int corrections, string? reason)
        => new()
        {
            RunId = run.RunId,
            Success = run.Status == RunStatus.Completed,
            FinalResponse = finalResponse,
            Iterations = iterations,
            Corrections = corrections,
            Retries = run.Retries,
            Duration = run.Duration,
            Reason = reason,
            Status = run.Status,
            Run = run
        };
}
