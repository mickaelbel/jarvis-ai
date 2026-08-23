using System.Globalization;

namespace JarvisAI.Application.Search;

public static class CitationBuilder
{
    public static string Format(SearchResult result)
    {
        var confidence = ((int)Math.Round(result.Confidence * 100, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "%";
        var parts = new List<string> { result.Title };

        var source = result.Author ?? result.Source;
        if (!string.IsNullOrWhiteSpace(source))
            parts.Add($"Source: {source}");

        parts.Add(result.Url);

        var date = result.PublishedAt is { } published ? published.ToString("yyyy-MM-dd") : null;
        if (date != null)
            parts.Add(date);

        parts.Add($"confiance: {confidence}");

        return string.Join(" — ", parts);
    }

    public static IReadOnlyList<string> FormatAll(IEnumerable<SearchResult> results)
        => results.Select(Format).ToList();

    public static string FormatPlainList(IEnumerable<SearchResult> results)
        => string.Join("\n", FormatAll(results));
}
