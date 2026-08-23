using JarvisAI.Application.AI;

namespace JarvisAI.Application.Agents;

public sealed record AgentRequest
{
    public string Goal { get; init; } = string.Empty;
    public string? CommandText { get; init; }
    public string? Source { get; init; }
    public Guid? CorrelationId { get; init; }
    public ModelSelectionMode Mode { get; init; } = ModelSelectionMode.Auto;
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    public int MaxIterations { get; init; } = 8;
    public TimeSpan? Timeout { get; init; }
    public bool AllowParallelTools { get; init; }
}
