namespace JarvisAI.Application.Agents;

public sealed record AutonomousLoopResult(
    bool Success,
    string FinalResponse,
    int Iterations,
    int Corrections,
    string? Reason);
