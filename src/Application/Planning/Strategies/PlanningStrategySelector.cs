using JarvisAI.Application.Context;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Planning.Strategies;

public sealed class PlanningStrategySelector : IPlanningStrategySelector
{
    private static readonly string[] ComputerUseKeywords =
    {
        "ordinateur", "écran", "ecran", "bureau", "clic", "clique", "click", "computer",
        "screen", "desktop", "window", "fenêtre", "fenetre", "interface", "ui", "souris",
        "mouse", "double-clic", "keyboard", "clavier"
    };

    private static readonly string[] AutomationKeywords =
    {
        "automatis", "répétit", "repete", "recurring", "chaque jour", "quotidien",
        "cron", "planifie", "schedule", "script", "batch", "automate", "routine"
    };

    private static readonly string[] ResearchKeywords =
    {
        "recherch", "research", "cherche", "look up", "find", "compare", "compare",
        "résume", "resume", "summarize", "actualité", "news", "qu'est-ce que", "info",
        "informations", "détail", "detail", "quelle est", "qui est"
    };

    private readonly IReadOnlyDictionary<PlanningStrategyKind, IPlanningStrategy> _strategies;
    private readonly ILogger<PlanningStrategySelector> _logger;

    public PlanningStrategySelector(IEnumerable<IPlanningStrategy> strategies, ILogger<PlanningStrategySelector> logger)
    {
        _strategies = strategies.ToDictionary(s => s.Kind);
        _logger = logger;
    }

    public PlanningStrategyKind Select(string goal, ContextBundle context)
    {
        var normalized = (goal ?? string.Empty).ToLowerInvariant();

        if (ContainsAny(normalized, ComputerUseKeywords))
            return PlanningStrategyKind.ComputerUse;
        if (ContainsAny(normalized, AutomationKeywords))
            return PlanningStrategyKind.Automation;
        if (ContainsAny(normalized, ResearchKeywords))
            return PlanningStrategyKind.Research;
        if (normalized.Length <= 60)
            return PlanningStrategyKind.Simple;

        return PlanningStrategyKind.Complex;
    }

    public IPlanningStrategy GetStrategy(PlanningStrategyKind kind)
    {
        if (_strategies.TryGetValue(kind, out var strategy))
            return strategy;

        _logger.LogWarning("[PlanningStrategySelector] No strategy registered for {Kind}, falling back to complex", kind);
        return _strategies[PlanningStrategyKind.Complex];
    }

    private static bool ContainsAny(string text, string[] keywords)
        => keywords.Any(text.Contains);
}
