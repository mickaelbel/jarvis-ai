using System.Text.RegularExpressions;

namespace JarvisAI.Application.Search;

public sealed class OfficialSiteDetector
{
    private static readonly string[] SchoolKeysByLength = OfficialSiteCatalog.SchoolKeys.OrderByDescending(k => k.Length).ToArray();
    private static readonly string[] ChannelKeysByLength = OfficialSiteCatalog.ChannelKeys.OrderByDescending(k => k.Length).ToArray();
    private static readonly string[] RepoKeysByLength = OfficialSiteCatalog.RepoKeys.OrderByDescending(k => k.Length).ToArray();
    private static readonly string[] DomainKeysByLength = OfficialSiteCatalog.DomainKeys.OrderByDescending(k => k.Length).ToArray();

    public OfficialSiteMatch? ResolveOfficial(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance))
            return null;

        var text = utterance.Trim();
        var normalized = OfficialSiteCatalog.NormalizeKey(text);

        if (Uri.TryCreate(text, UriKind.Absolute, out var directUri) && directUri.Scheme is "http" or "https")
            return new OfficialSiteMatch { Name = directUri.Host, Url = text, Confidence = 0.9, MatchKind = "direct-url" };

        if (TryMatchSchools(text, out var schoolMatch))
            return schoolMatch;
        if (TryMatchChannel(text, out var channelMatch))
            return channelMatch;
        if (TryMatchRepo(text, out var repoMatch))
            return repoMatch;
        if (TryMatchDomain(text, out var domainMatch))
            return domainMatch;
        if (TryMatchCreatorChannel(text, out var creatorMatch))
            return creatorMatch;

        return null;
    }

    public bool IsOfficialUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return IsOfficialHost(uri.Host);
    }

    public bool IsOfficialHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var h = host.Trim().ToLowerInvariant();
        if (h.StartsWith("www.", StringComparison.Ordinal))
            h = h[4..];

        foreach (var domain in OfficialSiteCatalog.AllDomains)
        {
            if (OfficialSiteCatalog.HostMatchesDomain(h, domain) == domain)
                return true;
        }

        return false;
    }

    public string? FindMatchingOfficialDomain(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var h = host.Trim().ToLowerInvariant();
        if (h.StartsWith("www.", StringComparison.Ordinal))
            h = h[4..];

        foreach (var domain in OfficialSiteCatalog.AllDomains)
        {
            if (OfficialSiteCatalog.HostMatchesDomain(h, domain) == domain)
                return domain;
        }
        return null;
    }

    private static bool TryMatchSchools(string text, out OfficialSiteMatch match)
    {
        match = null!;
        foreach (var key in SchoolKeysByLength)
        {
            if (ContainsWord(text, key))
            {
                OfficialSiteCatalog.TryGetSchoolUrl(key, out var url);
                match = new OfficialSiteMatch { Name = key, Url = url!, Confidence = 0.99, MatchKind = "school" };
                return true;
            }
        }
        return false;
    }

    private static bool TryMatchChannel(string text, out OfficialSiteMatch match)
    {
        match = null!;
        if (!text.Contains("youtube", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("chaîne", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("channel", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var key in ChannelKeysByLength)
        {
            if (ContainsWord(text, key))
            {
                OfficialSiteCatalog.TryGetOfficialChannel(key, out var url);
                match = new OfficialSiteMatch { Name = key, Url = url!, Confidence = 0.98, MatchKind = "youtube-channel" };
                return true;
            }
        }
        return false;
    }

    private static bool TryMatchRepo(string text, out OfficialSiteMatch match)
    {
        match = null!;
        var mentionsGithub = text.Contains("github", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("repo", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("repository", StringComparison.OrdinalIgnoreCase);
        if (!mentionsGithub)
            return false;

        var entity = ExtractEntityAfter(text, new[] { "github de", "github d'", "github du", "github", "repo de", "repo d'", "repo", "repos", "repository de", "repository" });
        if (!string.IsNullOrWhiteSpace(entity))
        {
            if (OfficialSiteCatalog.TryGetOfficialRepo(entity, out var repo))
            {
                match = new OfficialSiteMatch { Name = entity, Url = $"https://github.com/{repo}", Confidence = 0.99, MatchKind = "github-repo" };
                return true;
            }
        }

        foreach (var key in RepoKeysByLength)
        {
            if (ContainsWord(text, key))
            {
                OfficialSiteCatalog.TryGetOfficialRepo(key, out var repo);
                match = new OfficialSiteMatch { Name = key, Url = $"https://github.com/{repo}", Confidence = 0.99, MatchKind = "github-repo" };
                return true;
            }
        }
        return false;
    }

    private static bool TryMatchDomain(string text, out OfficialSiteMatch match)
    {
        match = null!;
        foreach (var key in DomainKeysByLength)
        {
            if (ContainsWord(text, key))
            {
                OfficialSiteCatalog.TryGetOfficialDomain(key, out var domain);
                match = new OfficialSiteMatch { Name = key, Url = $"https://{domain}", Confidence = 0.97, MatchKind = "official-domain" };
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Fallback "créateur" : une entité connue UNIQUEMENT comme chaîne YouTube (pas de
    /// site officiel au catalogue) est résolue vers sa chaîne même sans le mot
    /// "youtube"/"chaîne". Ex. "ouvre MrBeast" → youtube.com/@MrBeast.
    /// </summary>
    private static bool TryMatchCreatorChannel(string text, out OfficialSiteMatch match)
    {
        match = null!;
        foreach (var key in ChannelKeysByLength)
        {
            if (!ContainsWord(text, key))
                continue;
            if (OfficialSiteCatalog.DomainKeys.Any(d => string.Equals(d, key, StringComparison.OrdinalIgnoreCase)))
                continue;
            OfficialSiteCatalog.TryGetOfficialChannel(key, out var url);
            match = new OfficialSiteMatch { Name = key, Url = url!, Confidence = 0.97, MatchKind = "youtube-channel" };
            return true;
        }
        return false;
    }

    private static string? ExtractEntityAfter(string text, string[] markers)
    {
        foreach (var marker in markers)
        {
            var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                continue;
            var rest = text[(idx + marker.Length)..];
            rest = rest.Trim().TrimStart(' ', '\'', '"', '(', '[');
            rest = rest.Split(new[] { " et ", " sur ", " dans " }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (rest.Length > 0 && rest.Length < 40)
                return rest;
        }
        return null;
    }

    public static bool ContainsWord(string text, string key)
    {
        var idx = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return false;
        var before = idx == 0 || char.IsWhiteSpace(text[idx - 1]) || text[idx - 1] is '\'' or '-' or '(' or '"';
        var end = idx + key.Length;
        var after = end >= text.Length || char.IsWhiteSpace(text[end]) || text[end] is 's' or '\'' or ')' or '"' or '.' or ',' or '!';
        return before && after;
    }
}
