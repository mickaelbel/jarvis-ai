namespace JarvisAI.Application.Search;

public sealed class SearchResultDeduper
{
    private static readonly string[] TrackingParams =
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "gclid", "fbclid", "ref", "ref_src", "mc_cid", "mc_eid", "wickedid"
    };

    public IReadOnlyList<SearchResult> Dedupe(IEnumerable<SearchResult> results)
    {
        var best = new Dictionary<string, SearchResult>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var result in results)
        {
            var key = CanonicalKey(result.Url);
            if (key is null)
                continue;

            if (best.TryGetValue(key, out var existing))
            {
                if (result.Confidence > existing.Confidence ||
                    (result.Confidence.Equals(existing.Confidence) && IsBetter(existing, result)))
                    best[key] = result;
            }
            else
            {
                best[key] = result;
                order.Add(key);
            }
        }

        return order.Select(k => best[k]).ToList();
    }

    public static string? CanonicalKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme is not ("http" or "https"))
            return null;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        var segments = uri.AbsolutePath.TrimEnd('/');
        var path = string.IsNullOrEmpty(segments) ? "/" : segments.ToLowerInvariant();

        var query = uri.Query.TrimStart('?');
        var kept = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(q => !TrackingParams.Contains(q.Split('=')[0].ToLowerInvariant()))
            .OrderBy(q => q, StringComparer.Ordinal);
        var normalizedQuery = string.Join("&", kept);

        var key = $"{host}{path}";
        if (normalizedQuery.Length > 0)
            key += "?" + normalizedQuery;
        return key;
    }

    public static string CanonicalDomain(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return string.Empty;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];
        return host;
    }

    private static bool IsBetter(SearchResult current, SearchResult candidate)
    {
        if (current.IsOfficial != candidate.IsOfficial)
            return candidate.IsOfficial;
        if (current.IsVerified != candidate.IsVerified)
            return candidate.IsVerified;
        return !string.IsNullOrWhiteSpace(candidate.Snippet) && string.IsNullOrWhiteSpace(current.Snippet);
    }
}
