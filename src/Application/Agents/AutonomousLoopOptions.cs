namespace JarvisAI.Application.Agents;

public sealed record AutonomousLoopOptions
{
    public int MaxIterations { get; init; } = 8;
    public int MaxConsecutiveErrors { get; init; } = 3;
    public string? VerificationModel { get; init; }
}
