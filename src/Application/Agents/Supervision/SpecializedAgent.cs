using JarvisAI.Application.Context;

namespace JarvisAI.Application.Agents.Supervision;

/// <summary>Grand type d'agent spécialisé.</summary>
public enum AgentKind
{
    /// <summary>Agent générique raisonnant sur n'importe quel objectif.</summary>
    General,

    /// <summary>Agent de recherche : collecte, analyse et synthétise de l'information.</summary>
    Research,

    /// <summary>Agent de code : écrit, modifie et exécute du code via le terminal/fichiers.</summary>
    Coding,

    /// <summary>Agent d'utilisation de l'ordinateur : agit via souris/clavier/écran.</summary>
    Computer,

    /// <summary>Agent vision : décrit et raisonne sur des images / l'écran.</summary>
    Vision
}

/// <summary>Stratégie de plan associée en interne à un agent spécialisé.</summary>
public enum SpecializedStrategy
{
    Simple,
    Complex,
    Research,
    ComputerUse,
    Automation,
    Coding
}

/// <summary>
/// Descripteur d'un agent spécialisé : type, nom, périmètre d'outils autorisés et
/// stratégie de plan associée. Les agents dépendent d'INTERFACES de haut niveau ;
/// la sélection d'un agent ne fait que spécialiser comment le superviseur construit
/// le plan et quels outils il propose.
/// </summary>
public sealed record SpecializedAgentDescriptor(
    AgentKind Kind,
    string Name,
    string Description,
    SpecializedStrategy Strategy,
    IReadOnlyList<string>? AllowedToolCategories = null);

/// <summary>
/// Sélecteur d'agents spécialisés : à partir de l'objectif et du contexte (outils
/// disponibles), choisit l'agent le plus pertinent. Approche par mots-clés + heuristique,
/// KISS, remplaçable par un sélecteur appris sans changer le superviseur.
/// </summary>
public interface ISpecializedAgentSelector
{
    SpecializedAgentDescriptor Select(string goal, ContextBundle context);
}

public sealed class SpecializedAgentSelector : ISpecializedAgentSelector
{
    private static readonly string[] CodingKeywords =
    {
        "code", "script", "programme", "python", "function", "bug", "corrige", "debug",
        "écris un", "ecris un", "implémente", "implemente", "refactor", "compile", "compile",
        "classe", "méthode", "methode", "fonction", "github", "dépôt", "depot", "syntax"
    };

    private static readonly string[] ComputerKeywords =
    {
        "écran", "ecran", "souris", "mouse", "clic", "clique", "click", "clavier", "keyboard",
        "fenêtre", "fenetre", "window", "ordinateur", "bureau", "desktop", "interface", "ui"
    };

    private static readonly string[] VisionKeywords =
    {
        "image", "photo", "screenshot", "capture d'écran", "decris cette", "décris cette",
        "que vois-tu", "vision", "logo", "schéma", "schema", "graphique"
    };

    private static readonly string[] ResearchKeywords =
    {
        "recherch", "research", "cherche", "look up", "find", "compare", "compare",
        "summarize", "actualité", "news", "quelle est", "qui est", "info", "informations"
    };

    private readonly IReadOnlyDictionary<AgentKind, SpecializedAgentDescriptor> _catalog;

    public SpecializedAgentSelector()
    {
        _catalog = new Dictionary<AgentKind, SpecializedAgentDescriptor>
        {
            [AgentKind.Research] = new(AgentKind.Research, "Research Agent",
                "Collecte et synthétise de l'information (web, fichiers, documents).",
                SpecializedStrategy.Research, new[] { "web", "browser", "filesystem", "memory", "system", "ai" }),
            [AgentKind.Coding] = new(AgentKind.Coding, "Coding Agent",
                "Écrit, corrige et exécute du code, lit et manipule des fichiers.",
                SpecializedStrategy.Coding, new[] { "terminal", "filesystem", "dev", "web", "memory", "system" }),
            [AgentKind.Computer] = new(AgentKind.Computer, "Computer Agent",
                "Agit sur l'ordinateur via la souris, le clavier et l'observation d'écran.",
                SpecializedStrategy.ComputerUse, new[] { "computer_use", "vision", "system", "memory" }),
            [AgentKind.Vision] = new(AgentKind.Vision, "Vision Agent",
                "Observe et décrit des images / l'écran avec le modèle de vision.",
                SpecializedStrategy.Complex, new[] { "vision", "computer_use", "web", "memory", "system" }),
        };
    }

    public SpecializedAgentDescriptor Select(string goal, ContextBundle context)
    {
        var normalized = (goal ?? string.Empty).ToLowerInvariant();

        if (ContainsAny(normalized, CodingKeywords) && HasToolCategory(context, "terminal"))
            return _catalog[AgentKind.Coding];
        if (ContainsAny(normalized, ComputerKeywords) && HasToolCategory(context, "computer_use"))
            return _catalog[AgentKind.Computer];
        if (ContainsAny(normalized, VisionKeywords) && HasToolCategory(context, "vision"))
            return _catalog[AgentKind.Vision];
        if (ContainsAny(normalized, ResearchKeywords))
            return _catalog[AgentKind.Research];

        return new SpecializedAgentDescriptor(AgentKind.General, "General Agent",
            "Agent générique capable de traiter tout objectif.",
            SpecializedStrategy.Complex);
    }

    private static bool HasToolCategory(ContextBundle context, string category)
        => context.AvailableTools.Any(t =>
            t.Category.Equals(category, StringComparison.OrdinalIgnoreCase)
            || t.Name.Equals(category, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string text, string[] keywords)
        => keywords.Any(text.Contains);
}
