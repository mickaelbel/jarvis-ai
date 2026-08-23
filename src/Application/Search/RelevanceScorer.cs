using System.Text.RegularExpressions;

namespace JarvisAI.Application.Search;

public static class RelevanceScorer
{
    private static readonly Regex WordSplit = new(@"\W+", RegexOptions.Compiled);

    public static double Score(string query, string title, string snippet)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 0.0;

        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0)
            return 0.0;

        var textTokens = Tokenize((title ?? string.Empty) + " " + (snippet ?? string.Empty));
        if (textTokens.Count == 0)
            return 0.0;

        var titleTokens = Tokenize(title ?? string.Empty);

        var allMatch = queryTokens.Count(q => textTokens.Contains(q));
        var titleMatch = queryTokens.Count(q => titleTokens.Contains(q));

        var coverage = (double)allMatch / queryTokens.Count;
        var titleBonus = titleMatch > 0 ? 0.25 * Math.Min(1.0, (double)titleMatch / queryTokens.Count) : 0.0;
        var phrase = queryTokens.Count >= 2 &&
                     (title ?? string.Empty).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
            ? 0.15
            : 0.0;

        return Math.Clamp(coverage * 0.75 + titleBonus + phrase, 0.0, 1.0);
    }

    private static List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();
        return WordSplit.Split(text)
            .Select(w => w.ToLowerInvariant())
            .Where(w => w.Length >= 3 && !TopicExtractor.StopWords.Contains(w))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
