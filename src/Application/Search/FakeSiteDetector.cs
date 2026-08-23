using System.Collections.Concurrent;

namespace JarvisAI.Application.Search;

public sealed class FakeSiteDetector
{
    private readonly ConcurrentDictionary<string, string?> _closestDomainCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SuspiciousTokens =
    {
        "-official", "official-", "-login", "login-", "-support", "support-",
        "-secure", "secure-", "-verify", "verify-", "-update", "update-",
        "-help", "help-", "-account", "account-", "-signin", "signin-",
        "verification", "security-center", "customer-care", "service-", "-service"
    };

    private static readonly string[] SuspiciousTlds =
    {
        ".xyz", ".top", ".club", ".online", ".site", ".live", ".work", ".shop",
        ".icu", ".click", ".download", ".stream", ".gq", ".cf", ".ga", ".ml", ".tk"
    };

    private static readonly Dictionary<char, char> Homoglyphs = new()
    {
        ['0'] = 'o', ['o'] = 'o', ['1'] = 'l', ['l'] = 'l', ['i'] = 'l',
        ['!'] = 'l', ['|'] = 'l', ['5'] = 's', ['5'] = 's', ['s'] = 's',
        ['8'] = 'b', ['3'] = 'e', ['4'] = 'a', ['@'] = 'a', ['¢'] = 'c',
        ['ɑ'] = 'a', ['ō'] = 'o', ['е'] = 'e', ['а'] = 'a', ['с'] = 'c',
        ['р'] = 'p', ['о'] = 'o', ['в'] = 'b', ['н'] = 'h', ['т'] = 't',
        ['х'] = 'x', ['і'] = 'i', ['ѕ'] = 's', ['у'] = 'y', ['р'] = 'p'
    };

    public double ComputeTrustScore(Uri uri)
    {
        if (uri == null)
            return 0.0;
        if (uri.Scheme == "http" && OfficialSiteCatalog.AllDomains.Any(d => OfficialSiteCatalog.HostMatchesDomain(uri.Host, d) == d))
            return 0.3;

        var host = NormalizeHost(uri.Host);
        if (string.IsNullOrEmpty(host))
            return 0.0;

        foreach (var domain in OfficialSiteCatalog.AllDomains)
        {
            if (OfficialSiteCatalog.HostMatchesDomain(host, domain) == domain)
                return 1.0;
        }

        var score = 0.85;

        foreach (var token in SuspiciousTokens)
        {
            if (host.Contains(token, StringComparison.OrdinalIgnoreCase))
                score -= 0.4;
        }

        foreach (var tld in SuspiciousTlds)
        {
            if (host.EndsWith(tld, StringComparison.OrdinalIgnoreCase))
                score -= 0.4;
        }

        var closest = FindClosestOfficialDomain(host);
        if (closest != null)
        {
            var distance = Levenshtein(host, closest);
            if (distance <= 2)
                score = Math.Min(score, 0.05);
            else if (distance <= 4)
                score = Math.Min(score, 0.3);
        }

        return Math.Clamp(score, 0.0, 1.0);
    }

    public bool IsLikelyFake(Uri uri)
        => uri != null && ComputeTrustScore(uri) < 0.5;

    public bool IsLikelyFake(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsLikelyFake(uri);

    public string? FindClosestOfficialDomain(string host)
    {
        var normalized = NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized))
            return null;

        return _closestDomainCache.GetOrAdd(normalized, FindClosestCore);
    }

    private static string? FindClosestCore(string normalized)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var domain in OfficialSiteCatalog.AllDomains)
        {
            var d = Levenshtein(normalized, domain);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = domain;
            }
        }
        return bestDistance <= 5 ? best : null;
    }

    private static string NormalizeHost(string host)
    {
        var h = host.Trim().ToLowerInvariant();
        if (h.StartsWith("www.", StringComparison.Ordinal))
            h = h[4..];
        if (h.StartsWith("https://", StringComparison.Ordinal))
            h = h[8..];
        if (h.StartsWith("http://", StringComparison.Ordinal))
            h = h[7..];
        return h;
    }

    private static int Levenshtein(string a, string b)
    {
        if (string.IsNullOrEmpty(a))
            return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b))
            return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
