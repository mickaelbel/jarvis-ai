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
    private static readonly string[] AlwaysInclude = { "system_info", "date_time", "memory", "computer_action", "terminal" };

    private static readonly string[] ExecutionIntentWords =
    {
        "exécute", "execute", "exécut", "run", "lance", "lancer", "ouvre", "ouvrir", "start",
        "fais", "faites", "créé", "cree", "create", "installe", "install", "supprime", "delete",
        "commande", "command", "terminal", "cli", "computer", "ordinateur", "écran", "ecran",
        "screen", "desktop", "window", "fenêtre", "fenetre", "automate", "analyse", "analyze",
        "clique", "clic", "bouton", "souris", "clavier", "tape", "sélectionne", "selectionne",
        "déplace", "deplace", "ferme", "rolle", "scroll", "appuie", "press", "click",
        "remplis", "remplir", "colorie", "peins", "fond", "paint", "photoshop", "excel", "word",
        "blender", "interface", "menu", "barre", "onglet", "fenêtre", "noir", "blanc", "rouge"
    };

    private static readonly string[] HighRiskTools = { "terminal", "computer", "computer_use", "process", "windows" };

    // Requêtes explicites de génération d'image.
    private static readonly string[] ImageGenIntentWords =
    {
        "génère une image", "genere une image", "génère-moi", "genere-moi", "crée une image",
        "cree une image", "crée-moi", "cree-moi", "dessine", "create an image", "generate an image",
        "image de", "une image", "une photo de", "a picture of", "draw me"
    };

    // Mots décrivant une scène visuelle : déclenche la génération même sans mot « image ».
    private static readonly string[] VisualSceneWords =
    {
        "dans les montagnes", "au bord", "autour du lac", "autour d'un lac", "sur la plage",
        "ciel bleu", "ciel", "sunset", "coucher de soleil", "lever de soleil", "à l'aube",
        "paysage", "landscape", "portrait de", "dans l'espace", "soleil", "nuages",
        "another picture", "photo of a", "an image of"
    };

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

        if (goalTokens.Count == 0)
        {
            var always = allTools.Where(t => AlwaysInclude.Contains(t.Name)).ToList();
            return new ToolSelectionResult(always, always.ToDictionary(t => t.Name, _ => 5, StringComparer.Ordinal));
        }

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

        // Génération d'images : le pruning purement par mots-clés retire image_generator
        // pour une simple description de scène (« une Pagani au bord d'un lac »). On le
        // force à être proposé dès que l'intention est visuelle, sinon le modèle ne le voit
        // jamais et répond en texte sans générer d'image.
        if (allTools.Any(t => t.Name == "image_generator") && HasImageIntent(goalLower))
        {
            var exists = selected.Any(p => p.Tool.Name == "image_generator");
            if (exists)
                selected = selected
                    .Select(p => p.Tool.Name == "image_generator" ? (p.Tool, Math.Max(p.Score, 3)) : p)
                    .ToList();
            else
                selected.Add((allTools.First(t => t.Name == "image_generator"), 3));
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

    private static bool HasImageIntent(string goalLower)
    {
        if (goalLower.Length == 0) return false;
        if (ImageGenIntentWords.Any(goalLower.Contains)) return true;
        return VisualSceneWords.Any(goalLower.Contains);
    }
}
