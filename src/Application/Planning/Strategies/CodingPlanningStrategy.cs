using JarvisAI.Application.Context;
using JarvisAI.Application.Planning;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Planning.Strategies;

/// <summary>
/// Stratégie de plan pour les tâches de code : lecture/analyse → modification
/// → exécution/vérification → synthèse. Chaque étape est vérifiable.
/// </summary>
public sealed class CodingPlanningStrategy : PlanningStrategyBase
{
    public override PlanningStrategyKind Kind => PlanningStrategyKind.Coding;

    public CodingPlanningStrategy(IPlanner planner, ILogger<CodingPlanningStrategy> logger)
        : base(planner, logger)
    {
    }

    protected override string BuildInstructions(string goal, ContextBundle context)
        => "This is a coding / software task. The plan MUST start with a step that reads or understands the relevant " +
           "code/files, then a step that makes the change, then a step that runs/verifies the change (build, run, test), " +
           "and finally a concise final answer reporting what was done. Each change must be verifiable. Use the terminal " +
           "and filesystem tools to inspect and validate real state.";
}
