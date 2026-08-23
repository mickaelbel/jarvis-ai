using JarvisAI.Application.Context;
using JarvisAI.Application.Planning;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Planning.Strategies;

public sealed class AutomationPlanningStrategy : PlanningStrategyBase
{
    public override PlanningStrategyKind Kind => PlanningStrategyKind.Automation;

    public AutomationPlanningStrategy(IPlanner planner, ILogger<AutomationPlanningStrategy> logger)
        : base(planner, logger)
    {
    }

    protected override string BuildInstructions(string goal, ContextBundle context)
        => "This is an automation task. Plan steps to create, configure and run a repeatable script or command, " +
           "then verify it works. Prefer deterministic, idempotent steps that can be safely re-run. Include a final " +
           "verification step and a summary of what was automated.";
}
