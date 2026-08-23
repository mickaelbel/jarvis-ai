using JarvisAI.Application.Context;
using JarvisAI.Application.Planning;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Planning.Strategies;

public sealed class ComplexPlanningStrategy : PlanningStrategyBase
{
    public override PlanningStrategyKind Kind => PlanningStrategyKind.Complex;

    public ComplexPlanningStrategy(IPlanner planner, ILogger<ComplexPlanningStrategy> logger)
        : base(planner, logger)
    {
    }

    protected override string BuildInstructions(string goal, ContextBundle context)
        => "This is a complex, multi-step task. Break it down into granular, atomic steps in logical execution order. " +
           "For each step identify the tool it requires (only from the available tools list) and include all necessary " +
           "parameters. Keep steps small enough that each one can be verified independently. End with a step that produces " +
           "the final answer to the user.";
}
