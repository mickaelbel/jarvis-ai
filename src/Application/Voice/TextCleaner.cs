using System.Text.RegularExpressions;

namespace JarvisAI.Application.Voice;

public static class TextCleaner
{
    private static readonly Regex CodeFenceRegex = new(@"```.*?```", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex InlineCodeRegex = new(@"`([^`]*)`", RegexOptions.Compiled);
    private static readonly Regex ListMarkerRegex = new(@"(?m)^\s*[-*+]\s+", RegexOptions.Compiled);
    private static readonly Regex MarkdownRegex = new(@"[*_#>|~\\\[\]\(\)]|\b-{2,}\b", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static string StripMarkdown(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var result = CodeFenceRegex.Replace(text, " ");
        result = InlineCodeRegex.Replace(result, "$1");
        result = ListMarkerRegex.Replace(result, " ");
        result = UrlRegex.Replace(result, " ");
        result = MarkdownRegex.Replace(result, " ");
        result = WhitespaceRegex.Replace(result, " ").Trim();

        return result;
    }
}
