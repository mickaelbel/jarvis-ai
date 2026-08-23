using System.Globalization;

namespace JarvisAI.Application.Search;

public static class TrustScoring
{
    public static double ProviderTrust(string provider)
    {
        switch (provider.Trim().ToLowerInvariant())
        {
            case "wikipedia":
            case "wikidata":
                return 0.95;
            case "github":
                return 0.95;
            case "arxiv":
                return 0.95;
            case "stackoverflow":
            case "stack exchange":
                return 0.90;
            case "google news":
                return 0.90;
            case "news":
            case "rss":
                return 0.88;
            case "youtube":
                return 0.85;
            case "nominatim":
            case "openstreetmap":
                return 0.85;
            case "duckduckgo":
                return 0.80;
            case "bing":
                return 0.80;
            case "reddit":
                return 0.60;
            default:
                return 0.75;
        }
    }

    public static double DomainTrust(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return 0.0;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        if (host.EndsWith(".gov", StringComparison.Ordinal) || host.EndsWith(".gouv.fr", StringComparison.Ordinal))
            return 0.98;
        if (host.EndsWith(".edu", StringComparison.Ordinal))
            return 0.95;

        foreach (var domain in OfficialSiteCatalog.AllDomains)
        {
            if (OfficialSiteCatalog.HostMatchesDomain(host, domain) == domain)
                return 1.0;
        }

        if (host == "wikipedia.org" || host.EndsWith(".wikipedia.org", StringComparison.Ordinal))
            return 0.95;
        if (host == "github.com")
            return 0.95;
        if (host == "stackoverflow.com")
            return 0.90;
        if (host == "arxiv.org")
            return 0.95;
        if (host == "openstreetmap.org")
            return 0.85;

        return 0.7;
    }

    public static double ComputeConfidence(SearchResult result, bool isVerified)
    {
        var baseTrust = ProviderTrust(result.Provider);
        var domainTrust = DomainTrust(result.Url);

        var score = (baseTrust * 0.6) + (domainTrust * 0.4);

        if (result.IsOfficial)
            score = Math.Max(score, 0.9);
        if (isVerified)
            score += 0.05;
        if (!string.IsNullOrWhiteSpace(result.Author) && result.Type is SearchResultType.Video or SearchResultType.Channel or SearchResultType.Repository)
            score += 0.05;
        if (result.PublishedAt is { } published)
        {
            var age = DateTimeOffset.UtcNow - published;
            if (age < TimeSpan.FromDays(7))
                score += 0.05;
            else if (age < TimeSpan.FromDays(60))
                score += 0.02;
            else if (age > TimeSpan.FromDays(730))
                score -= 0.03;
        }
        if (result.ExtraScore is { } extra)
            score += extra;

        return Math.Clamp(score, 0.0, 1.0);
    }

    public static double NormalizedAgreement(int agreeingSources, int totalSources)
    {
        if (totalSources <= 1)
            return agreeingSources >= 1 ? 0.7 : 0.0;
        return Math.Clamp((double)agreeingSources / totalSources, 0.0, 1.0);
    }
}
