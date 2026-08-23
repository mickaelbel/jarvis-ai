using JarvisAI.Application.Context;

namespace JarvisAI.Application.Planning;

public interface IPlanningStrategy
{
    PlanningStrategyKind Kind { get; }
    string Name { get; }
    Task<Plan> CreatePlanAsync(string goal, ContextBundle context, CancellationToken cancellationToken = default);
}

public interface IPlanningStrategySelector
{
    PlanningStrategyKind Select(string goal, ContextBundle context);
    IPlanningStrategy GetStrategy(PlanningStrategyKind kind);
}
