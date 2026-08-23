using JarvisAI.Application.Planning;

namespace JarvisAI.Application.Reasoning;

public interface IReasoningEngine
{
    Task<ReasoningContext> AnalyzeGoalAsync(string goal, IReadOnlyList<string> availableTools, CancellationToken cancellationToken = default);
    Task<ThoughtStep> NextThoughtAsync(ReasoningContext context, CancellationToken cancellationToken = default);
    Task<Plan> GeneratePlanFromReasoningAsync(ReasoningContext context, CancellationToken cancellationToken = default);
}
