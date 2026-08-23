using System.Text.RegularExpressions;

namespace JarvisAI.Application.Search;

public sealed class OpenActionResolver
{
    private static readonly string[] OpenVerbs =
    {
        "ouvre", "ouvrir", "ouvre-moi", "ouvre moi", "lance", "va sur", "va voir",
        "open", "go to", "navigate to", "prends-moi", "prends moi", "montre-moi",
        "montre moi", "affiche", "va directement sur"
    };

    private readonly OfficialSiteDetector _officialDetector = new();

    public OpenAction Resolve(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance))
            return new OpenAction { Confidence = 0 };

        var text = utterance.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var directUri) && directUri.Scheme is "http" or "https")
            return new OpenAction { DirectUrl = text, Confidence = 1.0, Kind = "direct-url" };

        var official = _officialDetector.ResolveOfficial(text);
        if (official != null)
            return new OpenAction { DirectUrl = official.Url, Confidence = official.Confidence, Kind = official.MatchKind, Name = official.Name };

        var clean = StripVerbs(text);
        if (string.IsNullOrWhiteSpace(clean))
            clean = text;

        if (LooksLikeEntityUrl(clean, out var guess))
            return new OpenAction { DirectUrl = guess, Confidence = 0.5, Kind = "guess-domain", Name = clean };

        return new OpenAction
        {
            SearchQuery = clean,
            Confidence = 0.25,
            Kind = "search-fallback",
            Name = clean
        };
    }

    public static string StripVerbs(string text)
    {
        var t = " " + text.Trim() + " ";
        foreach (var verb in OpenVerbs.OrderByDescending(v => v.Length))
        {
            t = t.Replace(" " + verb + " ", " ", StringComparison.OrdinalIgnoreCase);
            t = t.Replace(" " + verb + " ", " ", StringComparison.OrdinalIgnoreCase);
        }
        t = Regex.Replace(t, @"\s+", " ").Trim();
        t = Regex.Replace(t, @"\b(le|la|les|de|du|des|au|aux|pour)\b", " ", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t;
    }

    public static bool LooksLikeEntityUrl(string entity, out string url)
    {
        url = string.Empty;
        var name = entity.Trim().ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("'", "-")
            .Replace("é", "e").Replace("è", "e").Replace("ê", "e").Replace("à", "a")
            .Replace("ç", "c").Replace("ô", "o").Replace("î", "i").Replace("û", "u")
            .Replace("â", "a").Replace("ë", "e").Replace("ï", "i").Replace("ù", "u");

        if (name.Length < 2 || name.Length > 60)
            return false;
        if (!Regex.IsMatch(name, "^[a-z0-9-]+$"))
            return false;
        if (name.Count(c => c == '-') > 1)
            return false;

        url = $"https://{name}.com";
        return true;
    }
}

public sealed class OpenAction
{
    public string? DirectUrl { get; init; }
    public string? SearchQuery { get; init; }
    public double Confidence { get; init; }
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
}
