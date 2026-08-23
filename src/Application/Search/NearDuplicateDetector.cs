using System.Text.RegularExpressions;

namespace JarvisAI.Application.Search;

public sealed class NearDuplicateDetector
{
    private const double TitleOverlapThreshold = 0.6;
    private static readonly Regex WhitespaceCollapse = new(@"\s+", RegexOptions.Compiled);

    public IReadOnlyList<SearchResult> Merge(IEnumerable<SearchResult> results)
    {
        var kept = new List<SearchResult>();

        foreach (var result in results)
        {
            var duplicate = kept.FirstOrDefault(k => IsNearDuplicate(k, result));
            if (duplicate is null)
            {
                kept.Add(result);
                continue;
            }

            if (IsBetter(result, duplicate))
            {
                var index = kept.IndexOf(duplicate);
                result.MergedCount = duplicate.MergedCount + 1;
                kept[index] = result;
            }
            else
            {
                duplicate.MergedCount += 1;
            }
        }

        return kept;
    }

    public static bool IsNearDuplicate(SearchResult a, SearchResult b)
    {
        if (ReferenceEquals(a, b))
            return false;
        if (string.Equals(a.Url, b.Url, StringComparison.OrdinalIgnoreCase))
            return true;

        var titleA = NormalizeTitle(a.Title);
        var titleB = NormalizeTitle(b.Title);
        if (string.IsNullOrWhiteSpace(titleA) || string.IsNullOrWhiteSpace(titleB))
            return false;

        if (string.Equals(titleA, titleB, StringComparison.Ordinal))
            return true;

        var wordsA = titleA.Split(' ');
        var wordsB = titleB.Split(' ');
        var overlap = wordsA.Intersect(wordsB, StringComparer.Ordinal).Count();
        var union = wordsA.Union(wordsB, StringComparer.Ordinal).Count();
        if (union == 0)
            return false;

        return (double)overlap / union >= TitleOverlapThreshold;
    }

    private static bool IsBetter(SearchResult candidate, SearchResult current)
    {
        if (candidate.IsOfficial != current.IsOfficial)
            return candidate.IsOfficial;
        if (!candidate.Confidence.Equals(current.Confidence))
            return candidate.Confidence > current.Confidence;
        if (candidate.IsVerified != current.IsVerified)
            return candidate.IsVerified;
        if (candidate.MergedCount != current.MergedCount)
            return candidate.MergedCount > current.MergedCount;
        return !string.IsNullOrWhiteSpace(candidate.Snippet) && string.IsNullOrWhiteSpace(current.Snippet);
    }

    private static string NormalizeTitle(string title)
        => WhitespaceCollapse.Replace(title.Trim().ToLowerInvariant(), " ").Trim(' ', '.', '!', '?', ',', ':', '"', '\'');
}
