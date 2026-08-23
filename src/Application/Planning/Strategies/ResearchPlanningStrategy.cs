using JarvisAI.Application.Context;
using JarvisAI.Application.Planning;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Planning.Strategies;

public sealed class ResearchPlanningStrategy : PlanningStrategyBase
{
    public override PlanningStrategyKind Kind => PlanningStrategyKind.Research;

    public ResearchPlanningStrategy(IPlanner planner, ILogger<ResearchPlanningStrategy> logger)
        : base(planner, logger)
    {
    }

    protected override string BuildInstructions(string goal, ContextBundle context)
        => "This is a research / information-gathering task. The plan MUST start with information gathering steps " +
           "(search, web lookup, file reads...), followed by an analysis/synthesis step that combines the findings, " +
           "and must end with a concise final answer to the user. Each distinct source or fact to check should be its " +
           "own step so the work is verifiable.";
}
