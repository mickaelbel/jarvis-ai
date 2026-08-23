using System.Text.RegularExpressions;

namespace JarvisAI.Application.Search;

public static class TopicExtractor
{
    public static IReadOnlyList<string> Extract(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Array.Empty<string>();

        var words = Regex.Split(title, @"\W+")
            .Select(w => w.ToLowerInvariant())
            .Where(w => w.Length >= 4 && !StopWords.Contains(w))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return words.Take(4).ToList();
    }

    public static IReadOnlyList<string> ExtractFromAll(IEnumerable<SearchResult> results)
        => results
            .Where(r => !string.IsNullOrWhiteSpace(r.Title))
            .SelectMany(r => Extract(r.Title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "your", "you", "are",
        "not", "but", "has", "have", "was", "were", "all", "can", "will", "just",
        "le", "la", "les", "des", "une", "un", "pour", "avec", "dans", "sur",
        "d'un", "d'une", "plus", "que", "qui", "est", "sont", "dans", "cette",
        "dernier", "dernière", "video", "vidéo", "official", "officiel"
    };
}
