using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Application.AI;

public enum QueryIntent
{
    FACTUAL,
    SUBJECTIVE,
    ADVICE,
    CREATIVE,
    CASUAL,
    TASK,
    CALCULATION,
    CURRENT_INFORMATION,
    UNKNOWN
}

public sealed class IntentClassification
{
    public QueryIntent Intent { get; init; }
    public bool IsSubjective => Intent == QueryIntent.SUBJECTIVE;
    public bool RequiresFactCheck => Intent == QueryIntent.CURRENT_INFORMATION || Intent == QueryIntent.FACTUAL;
    public string Reason { get; init; } = string.Empty;
    public float Confidence { get; init; }
}

public static class QueryIntentClassifier
{
    private static readonly string[] SubjectiveMarkersFr =
    {
        "selon toi", "à ton avis", "a ton avis", "pour toi", "pour vous",
        "tu préfères", "tu preferes", "vous préférez", "vous preferez",
        "le meilleur selon", "le plus beau selon", "le plus stylé selon",
        "le meilleur de tous les temps", "le plus beau de tous les temps",
        "quelle est ta préférée", "quelle est ta preferee",
        "quelle est la plus belle", "quelle est la meilleure",
        "quel est le meilleur", "quel est le plus beau",
        "quelle voiture choisirais", "quel jeu choisirais",
        "ton choix", "votre choix",
        "c'est quoi le meilleur", "c'est quoi le plus beau",
        "donne ton avis", "dis-moi ce que tu penses",
        "qu'est-ce que tu penses de", "qu est-ce que tu penses de",
        "aimes-tu", "aimes tu", "aimeriez-vous",
        "tu aimes", "tu aimes bien",
        "kiffer", "kiff", "préféré", "prefere",
        "le plus stylé", "le plus classe", "le plus classe",
        "le plus cool", "le plusawesome", "le plus incroyable",
        "le plus impressionnant", "le plus fascinant",
        "all time", "de tous les temps", "of all time",
        "best ever", "greatest of all time", "goat",
    };

    private static readonly string[] SubjectiveMarkersEn =
    {
        "according to you", "in your opinion", "your opinion",
        "do you prefer", "which do you prefer",
        "what do you think about", "what do you think of",
        "your favorite", "your favourite",
        "the best according to", "the most beautiful according to",
        "the best ever", "the greatest of all time",
        "the best of all time", "the most beautiful of all time",
        "which would you choose", "what would you pick",
        "do you like", "what's your take",
        "most beautiful car", "best car ever",
        "nicest car", "coolest car",
    };

    private static readonly string[] CurrentInfoMarkersFr =
    {
        "aujourd'hui", "maintenant", "actuellement", "en ce moment",
        "dernier", "dernière", "derniers", "dernières",
        "prix actuel", "prix actuels", "cours actuel",
        "météo", "meteo", "qu'il fait",
        "actualité", "actualités", "dernière news",
        "dernière nouvelle", "dernières nouvelles",
        "en direct", "en temps réel", "temps réel",
        "ce matin", "ce soir", "cette nuit",
        "demain", "hier",
        "quand est-ce que", "quand est-ce que",
        "prochain", "prochaine", "prochains", "prochaines",
        "combien coûte", "combien coute", "quel est le prix",
        "disponible", "en stock", "rupture",
        "sorti", "sortie", "released",
        "version actuelle", "dernière version",
    };

    private static readonly string[] FactualMarkersFr =
    {
        "qui est", "qui a", "qui est-ce qui",
        "quand", "combien", "quelle année", "quel année",
        "quelle est la définition", "quelle est la signification",
        "comment fonctionne", "comment ça marche",
        "où se trouve", "ou se trouve", "quel est le nom",
        "quelle est la capitale", "quel est le symbole",
        "combien de", "quel est le",
        "c'est quoi", "c est quoi", "définition de",
        "vrai ou faux", "est-ce vrai", "est-ce que",
        "c'est vrai que", "est-il vrai",
        "peux-tu confirmer", "peux tu confirmer",
        "c'est exact", "c'est correct",
    };

    private static readonly string[] AdviceMarkersFr =
    {
        "me conseilles-tu", "me conseilles tu",
        "que me conseilles", "que me conseillez",
        "tu me conseilles", "tu me conseillez",
        "quel conseil", "quels conseils",
        "je devrais", "je devrait",
        "faut-il", "faut il",
        "vaut-il mieux", "vaut il mieux",
        "est-ce que je dois", "est-ce que je devrais",
        "tu recommandes", "tu recommandez",
        "on devrait", "on devrait",
        "quel est le meilleur choix",
        "que choisir", "lequel choisir",
        "il vaut mieux",
    };

    private static readonly string[] CalculationMarkersFr =
    {
        "calcule", "additionne", "soustrais", "multiplie", "divise",
        "combien fait", "quel résultat",
        "résultat de", "somme de",
        "pourcentage", "pour cent",
        "moyenne de", "total de",
        "x=", "y=", "solve",
    };

    private static readonly string[] CreativeMarkersFr =
    {
        "écris", "ecris", "rédige", "redige", "compose",
        "invente", "imagine", "crée une histoire",
        "cree une histoire", "raconte",
        "dessine", "génère", "genere",
        "poème", "poeme", "chanson",
        "story", "write", "compose",
        "creative", "créatif",
    };

    private static readonly string[] TaskMarkersFr =
    {
        "ouvre", "ouvrir", "lance", "lancer",
        "ferme", "fermer", "arrête", "arrete",
        "installe", "installer", "télécharge", "telecharger",
        "supprime", "supprimer", "déplace", "deplace",
        "renomme", "renommer", "copie", "copier",
        "envoie", "envoyer", "cherche sur",
        "navigue", "va sur", "vas sur",
        "exécute", "execute", "lance le",
    };

    private static readonly Regex QuestionMarkRegex = new(@"\?\s*$", RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new(@"\b\d+\s*[+\-*/^%]\s*\d+\b", RegexOptions.Compiled);
    private static readonly Regex EmailRegex = new(@"[\w.-]+@[\w.-]+\.\w+", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IntentClassification Classify(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return new IntentClassification { Intent = QueryIntent.UNKNOWN, Reason = "empty", Confidence = 0f };

        var text = Normalize(userMessage);
        var hasQuestionMark = QuestionMarkRegex.IsMatch(text);

        // Email/URL = task
        if (EmailRegex.IsMatch(userMessage) || UrlRegex.IsMatch(userMessage))
            return new IntentClassification { Intent = QueryIntent.TASK, Reason = "email-or-url", Confidence = 0.9f };

        // Simple math
        if (NumberRegex.IsMatch(text))
            return new IntentClassification { Intent = QueryIntent.CALCULATION, Reason = "math-expression", Confidence = 0.95f };

        // Check subjective markers first (highest priority for the hallucination problem)
        if (ContainsAny(text, SubjectiveMarkersFr) || ContainsAny(text, SubjectiveMarkersEn))
            return new IntentClassification { Intent = QueryIntent.SUBJECTIVE, Reason = "subjective-marker", Confidence = 0.9f };

        // Current information
        if (ContainsAny(text, CurrentInfoMarkersFr))
            return new IntentClassification { Intent = QueryIntent.CURRENT_INFORMATION, Reason = "current-info-marker", Confidence = 0.85f };

        // Advice
        if (ContainsAny(text, AdviceMarkersFr))
            return new IntentClassification { Intent = QueryIntent.ADVICE, Reason = "advice-marker", Confidence = 0.85f };

        // Factual
        if (ContainsAny(text, FactualMarkersFr))
            return new IntentClassification { Intent = QueryIntent.FACTUAL, Reason = "factual-marker", Confidence = 0.8f };

        // Calculation keywords
        if (ContainsAny(text, CalculationMarkersFr))
            return new IntentClassification { Intent = QueryIntent.CALCULATION, Reason = "calculation-marker", Confidence = 0.85f };

        // Creative
        if (ContainsAny(text, CreativeMarkersFr))
            return new IntentClassification { Intent = QueryIntent.CREATIVE, Reason = "creative-marker", Confidence = 0.8f };

        // Task
        if (ContainsAny(text, TaskMarkersFr))
            return new IntentClassification { Intent = QueryIntent.TASK, Reason = "task-marker", Confidence = 0.8f };

        // Short messages without question mark are likely casual
        if (text.Length < 20 && !hasQuestionMark)
            return new IntentClassification { Intent = QueryIntent.CASUAL, Reason = "short-no-question", Confidence = 0.6f };

        // Default: if it has a question mark, assume factual
        if (hasQuestionMark)
            return new IntentClassification { Intent = QueryIntent.FACTUAL, Reason = "question-mark-default", Confidence = 0.5f };

        return new IntentClassification { Intent = QueryIntent.UNKNOWN, Reason = "no-marker", Confidence = 0.3f };
    }

    private static bool ContainsAny(string text, string[] markers)
    {
        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Normalize(string text)
    {
        var lowered = text.ToLowerInvariant();
        var formD = lowered.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(formD.Length);
        foreach (var c in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
