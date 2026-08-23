using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Application.Search;

/// <summary>
/// Garde-fou de pertinence : on n'ouvre JAMAIS un résultat qui n'a aucun rapport
/// avec ce qui a été demandé. Sans cette vérification, une recherche YouTube mal
/// ciblée renvoie la vidéo la plus populaire (ex. le RickRoll) et Jarvis l'ouvre
/// en croyant avoir trouvé la bonne — c'est le bug corrigé en P19.4.
/// </summary>
public static class ResultRelevance
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Français
        "le", "la", "les", "de", "du", "des", "un", "une", "et", "ou", "pour", "sur",
        "dans", "avec", "au", "aux", "ce", "cette", "ces", "son", "sa", "ses", "mon",
        "ma", "mes", "ton", "ta", "tes", "notre", "votre", "leur", "leurs", "qui",
        "que", "quoi", "comment", "pourquoi", "est", "sont", "fait", "faire", "peux",
        "peut", "moi", "toi", "lui", "elle", "on", "nous", "vous", "ils", "elles", "je",
        // Verbes d'action / navigation
        "ouvre", "ouvrir", "ouvre-moi", "ouvrir", "open", "regarde", "regarder",
        "watch", "montre", "montrer", "show", "affiche", "afficher", "cherche",
        "chercher", "search", "trouve", "trouver", "find", "va", "aller", "go",
        // Type de contenu (vidéo/chaîne/...)
        "vidéo", "video", "videos", "vidéos", "chaîne", "chaine", "channel",
        "playlist", "direct", "en", "live", "stream", "short", "shorts", "dernière",
        "derniere", "dernières", "dernieres", "dernier", "last", "nouvelle", "nouveau",
        "new", "nouveaux", "première", "premiere", "first",
        // Anglais
        "the", "a", "an", "of", "to", "and", "or", "for", "on", "in", "with", "at",
        "from", "by", "please", "s'il", "vous", "plait", "svp", "stp", "merci", "thanks",
        "thank", "bonjour", "salut", "hello", "hey"
    };

    /// <summary>
    /// Vérifie que le titre d'un résultat est en rapport avec la demande :
    /// soit la demande complète apparaît dans le titre, soit au moins un mot
    /// significatif (hors mots vides) de la demande apparaît dans le titre.
    /// </summary>
    public static bool Matches(string? title, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var normalizedQuery = Normalize(query);
        var normalizedTitle = Normalize(title);
        if (normalizedQuery.Length == 0)
            return true;

        if (normalizedTitle.Contains(normalizedQuery, StringComparison.Ordinal))
            return true;

        var queryTokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3 && !StopWords.Contains(t))
            .ToList();
        if (queryTokens.Count == 0)
            return true;

        var titleTokens = normalizedTitle
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        return queryTokens.Any(titleTokens.Contains);
    }

    private static string Normalize(string s)
    {
        s = s.ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        const string accents = "áàâäãåéèêëíìîïóòôöõúùûüçñýÿ";
        const string plain = "aaaaaaeeeeiiiiooooouuuucnyy";
        foreach (var c in s)
        {
            var idx = accents.IndexOf(c);
            if (idx >= 0)
                sb.Append(plain[idx]);
            else if (char.IsLetterOrDigit(c) || c == ' ')
                sb.Append(c);
            else
                sb.Append(' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
