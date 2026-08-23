using JarvisAI.Application.Context;
using JarvisAI.Application.Planning;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Planning.Strategies;

public sealed class ComputerUsePlanningStrategy : PlanningStrategyBase
{
    public override PlanningStrategyKind Kind => PlanningStrategyKind.ComputerUse;

    public ComputerUsePlanningStrategy(IPlanner planner, ILogger<ComputerUsePlanningStrategy> logger)
        : base(planner, logger)
    {
    }

    protected override string BuildInstructions(string goal, ContextBundle context)
        => "This is a computer / user-interface automation task. The plan MUST alternate between observation steps " +
           "(capturing the current screen or page state) and action steps (clicking, typing, navigating via the " +
           "computer_use / browser / terminal tools). NEVER assume a UI action succeeded: add a verification step after " +
           "each action. Keep each action atomic and reversible where possible.";
}
