namespace JarvisAI.Application.Search;

public sealed class QueryInterpreter
{
    private static readonly string[] VideoKeywords = { "vidéo", "video", "videos", "vidéos", "clip" };
    private static readonly string[] ChannelKeywords = { "chaîne", "chaine", "channel", "youtubeur", "youtubeuse" };
    private static readonly string[] PlaylistKeywords = { "playlist" };
    private static readonly string[] LiveKeywords = { "en direct", "live", "stream" };
    private static readonly string[] ShortKeywords = { "short", "shorts", "reels" };
    private static readonly string[] RepoKeywords = { "github", "repo", "repository", "dépôt", "depot git" };
    private static readonly string[] NewsKeywords = { "actualité", "actualites", "news", "dernières nouvelles", "breaking" };
    private static readonly string[] AcademicKeywords = { "arxiv", "scholar", "paper", "étude", "etude", "recherche académique", "publication", "thèse", "these", "hal " };
    private static readonly string[] LocalKeywords =
    {
        "restaurant", "restaurants", "garage", "hôtel", "hotel", "école", "ecole", "médecin", "medecin",
        "cinéma", "cinema", "près de", "pres de", "proche de", "autour de", "à côté de", "a cote de",
        "prépa", "prépas", "prepa", "prepas", "sti2d", "cpge", "lycée", "lycee", "collège", "college",
        "université", "universite", "fac", "bts", "dut", "licence", "master", "pizzeria", "pizza",
        "coiffeur", "pharmacie", "boulangerie", "boucherie", "supermarché", "supermarche", "dentiste",
        "avocat", "notaire", "banque", "bibliothèque", "bibliotheque", "salle de sport", "vétérinaire", "veterinaire",
        "japonais", "japonaise", "italien", "italienne", "chinois", "chinoise", "thai", "libanais", "libanaise",
        "marocain", "marocaine", "indien", "indienne", "mexicain", "mexicaine", "coreen", "vietnamien",
        "americain", "grec", "grecque", "espagnol", "espagnole", "portugais", "portugaise", "sushi", "kebab",
        "près de moi", "pres de moi", "près de chez moi", "chez moi", "autour de moi", "pas loin", "non loin"
    };
    private static readonly string[] OfficialKeywords = { "site officiel", "site officiel de", "official site", "official website" };
    private static readonly string[] RedditHints = { "avis", "recommandation", "recommandations", "vs", "vs.", "expérience", "retour", "retours", "témoignage", "temoignage", "sondage", "que pensez-vous", "reddit" };
    private static readonly string[] VideoHints = { "tutoriel", "tuto", "guide", "howto", "how to", "démo", "demo", "walkthrough" };
    private static readonly string[] TechHints = { "code", "github", "repo", "stack trace", "exception", "erreur", "bug", "fix", "issue", "stackoverflow", "stack overflow" };
    private static readonly string[] LocalHints =
    {
        "près de", "pres de", "proche de", "autour de", "à côté de", "a cote de", "restaurant",
        "hôtel", "hotel", "cinéma", "cinema", "garage", "médecin", "medecin", "école", "ecole",
        "prépa", "prépas", "prepa", "prepas", "sti2d", "cpge", "lycée", "lycee", "collège",
        "college", "université", "universite", "fac", "bts", "dut", "licence", "master", "pizzeria",
        "coiffeur", "pharmacie", "boulangerie", "boucherie", "supermarché", "supermarche", "dentiste",
        "japonais", "italien", "sushi", "kebab", "près de moi", "chez moi"
    };

    private static readonly string[] FrenchMarkers = { "le", "la", "les", "de", "du", "des", "et", "ou", "pour", "sur", "dans", "avec", "une", "un", "à", "au", "aux", "en", "mon", "ma", "mes", "ce", "cette", "ces", "sa", "son", "ses", "notre", "votre", "leur", "qui" };

    public SearchRequest Interpret(string utterance, int maxResults = 10)
    {
        var query = utterance?.Trim() ?? string.Empty;
        var language = DetectLanguage(query);
        var type = DetectType(query);

        var request = new SearchRequest
        {
            Query = CleanQuery(query),
            Language = language,
            Type = type,
            MaxResults = maxResults
        };
        return request;
    }

    public static SearchResultType DetectType(string query)
    {
        var q = " " + query.ToLowerInvariant() + " ";

        if (ContainsAny(q, OfficialKeywords) || (q.Contains(" site ", StringComparison.Ordinal) && q.Contains("officiel", StringComparison.Ordinal)))
            return SearchResultType.OfficialSite;

        if (ContainsAny(q, ShortKeywords))
            return SearchResultType.Short;
        if (ContainsAny(q, LiveKeywords))
            return SearchResultType.Live;
        if (ContainsAny(q, PlaylistKeywords))
            return SearchResultType.Playlist;
        if (ContainsAny(q, ChannelKeywords))
            return SearchResultType.Channel;
        if (ContainsAny(q, VideoKeywords))
            return SearchResultType.Video;
        if (ContainsAny(q, RepoKeywords))
            return SearchResultType.Repository;
        if (ContainsAny(q, NewsKeywords))
            return SearchResultType.News;
        if (ContainsAny(q, AcademicKeywords))
            return SearchResultType.Academic;
        if (ContainsAny(q, LocalKeywords))
            return SearchResultType.Local;

        return SearchResultType.General;
    }

    public static string? DetectLanguage(string query)
    {
        var q = " " + query.ToLowerInvariant() + " ";
        var frenchHits = FrenchMarkers.Count(m => q.Contains(" " + m + " ", StringComparison.Ordinal));
        if (frenchHits >= 1)
            return "fr";
        return null;
    }

    public static IReadOnlyList<string> SuggestProviders(SearchRequest request)
    {
        switch (request.Type)
        {
            case SearchResultType.Video:
            case SearchResultType.Channel:
            case SearchResultType.Playlist:
            case SearchResultType.Live:
            case SearchResultType.Short:
                return new[] { "youtube" };
            case SearchResultType.Repository:
                return new[] { "github" };
            case SearchResultType.News:
                return new[] { "news" };
            case SearchResultType.Academic:
                return new[] { "arxiv", "semantic_scholar", "hal", "pubmed", "crossref", "wikipedia" };
            case SearchResultType.Local:
                return new[] { "nominatim" };
            default:
                return DefaultGeneralProviders(request.Query);
        }
    }

    /// <summary>
    /// Pour une requête générale, sélectionne intelligemment les providers en fonction
    /// du contenu de la requête : avis (Reddit), tutoriels (YouTube), technique (GitHub,
    /// StackOverflow), local (Nominatim), etc. On garde toujours les généralistes
    /// (DuckDuckGo, Bing, Wikipedia) comme socle.
    /// </summary>
    private static IReadOnlyList<string> DefaultGeneralProviders(string query)
    {
        var base_ = new List<string> { "duckduckgo", "bing", "wikipedia", "news" };
        var q = " " + query.ToLowerInvariant() + " ";
        if (ContainsAny(q, RedditHints)) base_.Add("reddit");
        if (ContainsAny(q, VideoHints))  base_.Add("youtube");
        if (ContainsAny(q, TechHints))   base_.Add("github"); // also added if TechHints
        if (ContainsAny(q, TechHints))   base_.Add("stackoverflow");
        if (ContainsAny(q, LocalHints))  base_.Add("nominatim");
        return base_;
    }

    private static string CleanQuery(string query)
        => query.Trim();

    private static bool ContainsAny(string q, string[] keywords)
        => keywords.Any(k => q.Contains(k, StringComparison.Ordinal));
}
