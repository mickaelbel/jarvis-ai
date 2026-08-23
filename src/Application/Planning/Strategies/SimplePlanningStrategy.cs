using JarvisAI.Application.Context;
using JarvisAI.Application.Planning;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Planning.Strategies;

public sealed class SimplePlanningStrategy : PlanningStrategyBase
{
    public override PlanningStrategyKind Kind => PlanningStrategyKind.Simple;

    public SimplePlanningStrategy(IPlanner planner, ILogger<SimplePlanningStrategy> logger)
        : base(planner, logger)
    {
    }

    protected override string BuildInstructions(string goal, ContextBundle context)
        => "This is a simple, short task. Produce a minimal plan of 1 to 2 steps. " +
           "Do not over-decompose: only split if a tool call genuinely needs to happen before the final answer.";
}
