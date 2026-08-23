using System.Net;
using System.Text.RegularExpressions;

namespace JarvisAI.Application.Search;

public sealed record ContentExtractionResult(string Url, string Title, string Text, IReadOnlyList<string> Citations);

public static class ContentExtractor
{
    private static readonly string[] BoilerplatePatterns =
    {
        "cookie", "accepter les", "j'accepte", "mentions légales", "politique de confidentialité",
        "subscribe", "newsletter", "rechercher", "se connecter", "s'inscrire", "menu",
        "advertisement", "publicité", "cliquez ici pour continuer", "voir plus"
    };

    public static ContentExtractionResult? Extract(string? html, int maxWords = 200, string? url = null)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var title = ExtractTitle(html);
        var main = ExtractMainContainer(html);
        if (main is null)
            return null;

        var paragraphs = ExtractParagraphs(main);
        var text = Join(paragraphs, maxWords);
        var citations = ExtractCitations(main, url).Take(5).ToList();

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(title))
            return null;

        return new ContentExtractionResult(url ?? string.Empty, title ?? string.Empty, text, citations);
    }

    internal static string? ExtractTitle(string html)
    {
        var match = Regex.Match(html, "<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;
        var title = CleanText(match.Groups[1].Value);
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    internal static string? ExtractMainContainer(string html)
    {
        var cleaned = StripScriptsAndStyles(html);

        var candidates = new[] { "<article[^>]*>", "<main[^>]*>" };
        foreach (var pattern in candidates)
        {
            var match = Regex.Match(cleaned, pattern + ".*?</" + pattern.TrimEnd('[', '>') + ">", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (match.Success && Regex.IsMatch(match.Value, "<p[ >]", RegexOptions.IgnoreCase))
                return match.Value;
        }

        var container = Regex.Match(cleaned,
            "<(?:div|section)[^>]*(?:id|class)\\s*=\\s*[\"'][^\"']*(?:content|main|article|post|entry)[^\"']*[\"'][^>]*>.*?</(?:div|section)>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (container.Success && Regex.IsMatch(container.Value, "<p[ >]", RegexOptions.IgnoreCase))
            return container.Value;

        var body = Regex.Match(cleaned, "<body[^>]*>(.*?)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return body.Success ? body.Value : cleaned;
    }

    internal static IReadOnlyList<string> ExtractParagraphs(string main)
    {
        var results = new List<string>();
        var tags = Regex.Matches(main,
            "<(?:p|li|h[1-6]|blockquote)[^>]*>(.*?)</(?:p|li|h[1-6]|blockquote)>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match match in tags)
        {
            var text = CleanText(match.Groups[1].Value);
            if (text.Length < 40)
                continue;
            if (BoilerplatePatterns.Any(b => text.Contains(b, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (IsNavText(text))
                continue;
            results.Add(text);
        }
        return results;
    }

    internal static IReadOnlyList<string> ExtractCitations(string main, string? pageUrl)
    {
        var citations = new List<string>();
        Uri? pageUri = Uri.TryCreate(pageUrl, UriKind.Absolute, out var parsed) ? parsed : null;
        var pageHost = pageUri?.Host?.ToLowerInvariant();

        var links = Regex.Matches(main,
            "<a[^>]*href\\s*=\\s*[\"']([^\"']+)[\"'][^>]*>(.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match link in links)
        {
            var href = WebUtility.HtmlDecode(link.Groups[1].Value).Trim();
            if (href.StartsWith('#') || href.StartsWith("javascript:") || href.StartsWith("mailto:") || href.StartsWith("tel:"))
                continue;
            if (!Uri.TryCreate(href, UriKind.Absolute, out var linkUri))
            {
                if (pageUri is null)
                    continue;
                if (href.StartsWith("//"))
                {
                    if (!Uri.TryCreate("https:" + href, UriKind.Absolute, out linkUri))
                        continue;
                }
                else
                {
                    if (!Uri.TryCreate(pageUri, href, out linkUri))
                        continue;
                }
            }
            if (linkUri.Scheme is not ("http" or "https"))
                continue;
            if (pageHost is not null && string.Equals(linkUri.Host, pageHost, StringComparison.OrdinalIgnoreCase))
                continue;

            var label = CleanText(link.Groups[2].Value);
            if (label.Length < 3)
                label = linkUri.Host;
            citations.Add($"{label} ({linkUri.ToString()})");
            if (citations.Count >= 5)
                break;
        }
        return citations;
    }

    private static string StripScriptsAndStyles(string html)
        => Regex.Replace(html,
            "<(script|style|noscript|nav|header|footer|aside|form|iframe)[^>]*>.*?</\\1>",
            " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static bool IsNavText(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 3 ||
               (trimmed.Length <= 25 && !trimmed.Contains(' ') && !trimmed.Contains('.'));
    }

    private static string CleanText(string value)
    {
        var text = Regex.Replace(value, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }

    private static string Join(IReadOnlyList<string> paragraphs, int maxWords)
    {
        var joined = string.Join("\n\n", paragraphs);
        var words = joined.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= maxWords)
            return joined;
        return string.Join(" ", words.Take(maxWords)) + "…";
    }
}
