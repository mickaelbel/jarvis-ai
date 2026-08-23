using JarvisAI.Application.Context;
using JarvisAI.Application.Planning;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Planning.Strategies;

public abstract class PlanningStrategyBase : IPlanningStrategy
{
    protected readonly IPlanner Planner;
    protected readonly ILogger Logger;

    protected PlanningStrategyBase(IPlanner planner, ILogger logger)
    {
        Planner = planner;
        Logger = logger;
    }

    public abstract PlanningStrategyKind Kind { get; }

    public string Name => $"{Kind}Strategy";

    protected abstract string BuildInstructions(string goal, ContextBundle context);

    public virtual async Task<Plan> CreatePlanAsync(string goal, ContextBundle context, CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("[{Strategy}] Creating {StrategyName} plan for: {Goal}", Name, Name, goal);

        var tools = context.ToolNames;
        var instructions = BuildInstructions(goal, context);

        var plan = await Planner.CreatePlanWithInstructionsAsync(goal, instructions, tools, cancellationToken);

        if (plan is null || plan.Steps.Count == 0)
            plan = CreateFallbackPlan(goal);

        return plan;
    }

    protected virtual Plan CreateFallbackPlan(string goal)
        => new()
        {
            Goal = goal,
            Status = PlanStatus.Created,
            Steps = new List<PlanStep>
            {
                new()
                {
                    Index = 0,
                    Action = "Execute goal",
                    Description = goal,
                    ToolName = null
                }
            }
        };
}
