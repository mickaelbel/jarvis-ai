using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Web.Services;

public interface IContextualHelpService
{
    HelpContent GetHelp(string context, string? language = null);
    IReadOnlyList<HelpTopic> GetTopics(string? category = null);
    HelpTopic? GetTopic(string topicId);
    string SearchHelp(string query);
    IReadOnlyList<HelpTip> GetTips(string currentContext);
}

public sealed class ContextualHelpService : IContextualHelpService
{
    private readonly ILogger<ContextualHelpService> _logger;
    private readonly List<HelpTopic> _topics = new();

    public ContextualHelpService(ILogger<ContextualHelpService> logger)
    {
        _logger = logger;
        InitializeTopics();
    }

    public HelpContent GetHelp(string context, string? language = null)
    {
        var lower = context.ToLowerInvariant();
        var lang = language ?? "fr";

        var content = lower switch
        {
            "chat" => new HelpContent
            {
                Title = "Aide Chat",
                Content = @"## Chat avec Jarvis

**Commandes disponibles:**
- Tapez votre message normalement
- `@outil nom_outil` - Exécuter un outil
- `/clear` - Effacer la conversation
- `/help` - Afficher cette aide
- `/history` - Historique des conversations

**Raccourcis clavier:**
- `Ctrl+Enter` - Envoyer le message
- `Ctrl+L` - Effacer le chat
- `Ctrl+K` - Rechercher dans l'historique",
                Category = "Interface"
            },
            "tools" => new HelpContent
            {
                Title = "Aide Outils",
                Content = @"## Outils disponibles

**Système:**
- `terminal` - Exécuter des commandes
- `filesystem` - Gérer les fichiers
- `browser` - Contrôler le navigateur

**IA:**
- `chat` - Interagir avec le modèle
- `analyze` - Analyser du contenu
- `generate` - Générer du contenu

**Productivité:**
- `email` - Gérer les emails
- `calendar` - Gérer le calendrier
- `notes` - Prendre des notes",
                Category = "Outils"
            },
            "voice" => new HelpContent
            {
                Title = "Aide Voix",
                Content = @"## Commandes vocales

**Activation:**
- Dites « Jarvis » pour activer l'écoute
- Ou appuyez sur le bouton microphone

**Commandes courantes:**
- « Ouvre [application] »
- « Recherche [sujet] »
- « Envoie un email à [nom] »
- « Rappelle-moi de [tâche] »
- « Quelle heure est-il ? »

**Paramètres:**
- Ajustez le volume dans les paramètres
- Changez la voix dans Paramètres > Voix",
                Category = "Voix"
            },
            _ => new HelpContent
            {
                Title = "Aide Générale",
                Content = @"## Bienvenue dans Jarvis AI

Jarvis est votre assistant IA personnel. Voici ce que vous pouvez faire:

1. **Chat** - Posez des questions ou donnez des ordres
2. **Outils** - Utilisez les outils système et IA
3. **Voix** - Contrôlez Jarvis par la voix
4. **Automatisation** - Créez des workflows

Tapez `/help [sujet]` pour plus d'informations.",
                Category = "Général"
            }
        };

        return content;
    }

    public IReadOnlyList<HelpTopic> GetTopics(string? category = null)
    {
        if (category is null) return _topics;
        return _topics.Where(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public HelpTopic? GetTopic(string topicId)
        => _topics.FirstOrDefault(t => t.Id == topicId);

    public string SearchHelp(string query)
    {
        var lower = query.ToLowerInvariant();
        var results = _topics.Where(t =>
            t.Title.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
            t.Content.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
            t.Tags.Any(tag => tag.Contains(lower, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (!results.Any())
            return "Aucun résultat trouvé pour votre recherche.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Résultats pour « {query} »");
        sb.AppendLine();

        foreach (var result in results.Take(5))
        {
            sb.AppendLine($"### {result.Title}");
            sb.AppendLine(result.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public IReadOnlyList<HelpTip> GetTips(string currentContext)
    {
        var tips = new List<HelpTip>();
        var lower = currentContext.ToLowerInvariant();

        if (lower.Contains("chat"))
        {
            tips.Add(new HelpTip { Message = "Appuyez sur Ctrl+Enter pour envoyer rapidement", Importance = TipImportance.Low });
            tips.Add(new HelpTip { Message = "Utilisez /clear pour recommencer une conversation", Importance = TipImportance.Medium });
        }

        if (lower.Contains("tool"))
        {
            tips.Add(new HelpTip { Message = "Les outils sont exécutés automatiquement quand Jarvis les détecte", Importance = TipImportance.Low });
        }

        if (lower.Contains("voice"))
        {
            tips.Add(new HelpTip { Message = "Parlez clairement pour de meilleurs résultats", Importance = TipImportance.Medium });
        }

        return tips;
    }

    private void InitializeTopics()
    {
        _topics.AddRange(new[]
        {
            new HelpTopic
            {
                Id = "getting-started",
                Title = "Premiers pas",
                Content = "Guide de démarrage rapide pour Jarvis AI",
                Category = "Introduction",
                Tags = new() { "début", "start", "intro" }
            },
            new HelpTopic
            {
                Id = "chat-basics",
                Title = "Bases du chat",
                Content = "Comment utiliser le chat efficacement",
                Category = "Chat",
                Tags = new() { "chat", "message", "conversation" }
            },
            new HelpTopic
            {
                Id = "voice-commands",
                Title = "Commandes vocales",
                Content = "Liste des commandes vocales disponibles",
                Category = "Voix",
                Tags = new() { "voix", "voice", "microphone" }
            },
            new HelpTopic
            {
                Id = "tools-overview",
                Title = "Vue d'ensemble des outils",
                Content = "Tous les outils disponibles et leur utilisation",
                Category = "Outils",
                Tags = new() { "outils", "tools", "fonctions" }
            },
            new HelpTopic
            {
                Id = "troubleshooting",
                Title = "Résolution de problèmes",
                Content = "Solutions aux problèmes courants",
                Category = "Support",
                Tags = new() { "bug", "error", "problème" }
            }
        });
    }
}

public sealed class HelpContent
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = "";
}

public sealed class HelpTopic
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = "";
    public List<string> Tags { get; set; } = new();
}

public sealed class HelpTip
{
    public string Message { get; set; } = "";
    public TipImportance Importance { get; set; }
}

public enum TipImportance { Low, Medium, High }
