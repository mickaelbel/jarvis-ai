using JarvisAI.Application.Context;

namespace JarvisAI.Application.Tools;

public sealed class ToolSelectionResult
{
    public IReadOnlyList<ITool> Tools { get; }
    public IReadOnlyDictionary<string, int> Scores { get; }

    public ToolSelectionResult(IReadOnlyList<ITool> tools, IReadOnlyDictionary<string, int> scores)
    {
        Tools = tools;
        Scores = scores;
    }
}

public interface IToolSelectionService
{
    IReadOnlyList<ITool> Select(IReadOnlyList<ITool> allTools, string goal, ContextBundle? context = null);
    ToolSelectionResult SelectWithScores(IReadOnlyList<ITool> allTools, string goal, ContextBundle? context = null);
}

public sealed class ToolSelectionService : IToolSelectionService
{
    private static readonly string[] AlwaysInclude = { "system_info", "date_time", "memory" };

    private static readonly string[] ExecutionIntentWords =
    {
        "exécute", "execute", "exécut", "run", "lance", "lancer", "ouvre", "ouvrir", "start",
        "fais", "faites", "créé", "cree", "create", "installe", "install", "supprime", "delete",
        "commande", "command", "terminal", "cli", "computer", "ordinateur", "écran", "ecran",
        "screen", "desktop", "window", "fenêtre", "fenetre", "automate", "analyse", "analyze"
    };

    private static readonly string[] HighRiskTools = { "terminal", "computer", "computer_use", "process", "windows" };

    private const int DefaultLimit = 12;

    public IReadOnlyList<ITool> Select(IReadOnlyList<ITool> allTools, string goal, ContextBundle? context = null)
        => SelectWithScores(allTools, goal, context).Tools;

    public ToolSelectionResult SelectWithScores(IReadOnlyList<ITool> allTools, string goal, ContextBundle? context = null)
    {
        if (allTools is null || allTools.Count == 0)
            return new ToolSelectionResult(Array.Empty<ITool>(), new Dictionary<string, int>());

        var goalLower = (goal ?? string.Empty).ToLowerInvariant();
        var goalTokens = Tokenize(goalLower);
        var hasExecutionIntent = ExecutionIntentWords.Any(goalLower.Contains);

        var selected = new List<(ITool Tool, int Score)>();

        foreach (var tool in allTools)
        {
            var score = Score(tool, goalLower, goalTokens);
            var isHighRisk = HighRiskTools.Contains(tool.Name);

            if (isHighRisk && !hasExecutionIntent && score < 4)
                continue;

            if (AlwaysInclude.Contains(tool.Name))
                score = Math.Max(score, 3);

            if (goalTokens.Count == 0 && !AlwaysInclude.Contains(tool.Name))
                continue;

            selected.Add((tool, score));
        }

        var ranked = selected
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.Tool.Name, StringComparer.Ordinal)
            .Take(DefaultLimit)
            .ToList();

        var resultTools = ranked.Select(pair => pair.Tool).ToList();
        var resultScores = ranked.ToDictionary(pair => pair.Tool.Name, pair => pair.Score, StringComparer.Ordinal);

        return new ToolSelectionResult(resultTools, resultScores);
    }

    private static int Score(ITool tool, string goalLower, HashSet<string> goalTokens)
    {
        var score = 0;
        var name = tool.Name.ToLowerInvariant();
        var category = (tool.Category ?? string.Empty).ToLowerInvariant();
        var description = (tool.Description ?? string.Empty).ToLowerInvariant();

        foreach (var token in goalTokens)
        {
            if (token == name) score += 4;
            else if (name.Contains(token, StringComparison.Ordinal)) score += 3;

            if (category == token || category.Contains(token, StringComparison.Ordinal)) score += 2;

            if (description.Contains(token, StringComparison.Ordinal)) score += 1;
        }

        if (goalLower.Contains(name, StringComparison.Ordinal)) score += 2;

        return score;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = token.Trim(new[] { ',', '.', ';', ':', '!', '?', '\'', '"', '(', ')', '[', ']' });
            if (cleaned.Length < 2) continue;
            tokens.Add(cleaned);
        }
        return tokens;
    }
}
