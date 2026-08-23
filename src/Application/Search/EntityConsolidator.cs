namespace JarvisAI.Application.Search;

/// <summary>
/// Consolidation d'entités au niveau du résultat de recherche : au lieu de traiter
/// chaque URL indépendamment, on regroupe les candidats qui désignent la MÊME entité
/// (p. ex. une chaîne YouTube), on croise les providers, on privilégie la forme
/// canonique (@handle) et on choisit le représentant dont les métadonnées sont les
/// plus cohérentes — abonnés, vérification, ancienneté, nombre de vidéos.
///
/// Objectif : "chaîne MrBeast" doit aboutir à youtube.com/@MrBeast et jamais à
/// youtube.com/@user-XXXX. Le premier résultat d'un provider n'est jamais pris tel quel.
/// </summary>
public sealed class EntityConsolidator
{
    /// <summary>
    /// Consolide les résultats ; renvoie la liste avec UN REPRÉSENTANT par entité
    /// (pour les chaînes YouTube) + les autres résultats non-apparentés inchangés.
    /// </summary>
    public IReadOnlyList<SearchResult> Consolidate(
        IReadOnlyList<SearchResult> results,
        int providerContextCount = 0)
    {
        if (results.Count == 0)
            return results;

        var channelGroups = new Dictionary<string, List<SearchResult>>(StringComparer.Ordinal);
        var others = new List<SearchResult>();
        var distinctChannelProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in results)
        {
            var key = ChannelIdentity(r);
            if (key is null)
            {
                others.Add(r);
                continue;
            }
            if (!channelGroups.TryGetValue(key, out var group))
            {
                group = new List<SearchResult>();
                channelGroups[key] = group;
            }
            group.Add(r);
            if (!string.IsNullOrWhiteSpace(r.Provider))
                distinctChannelProviders.Add(r.Provider);
        }

        if (channelGroups.Count == 0)
            return results;

        var denominator = providerContextCount > 0
            ? providerContextCount
            : Math.Max(1, distinctChannelProviders.Count);

        var consolidated = new List<SearchResult>();
        foreach (var group in channelGroups.Values)
        {
            var representative = SelectRepresentative(group);
            if (representative is null)
                continue;

            var providersInGroup = group
                .Select(g => g.Provider)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var agreement = Math.Min(1.0, (double)providersInGroup / denominator);
            var preferredForm = group.Any(g => HasHandle(g.Url));

            representative.EntityAgreement = Math.Round(agreement, 3);

            if (preferredForm && !HasHandle(representative.Url))
            {
                // On privilégie la forme canonique @handle même si une autre URL
                // était le représentant métadonnées : la forme @handle est le
                // meilleur identifiant public officiel.
                var handle = group.FirstOrDefault(g => HasHandle(g.Url));
                if (handle is not null)
                {
                    handle.EntityAgreement = representative.EntityAgreement;
                    representative.EntityAgreement = null;
                    representative = handle;
                }
            }

            representative.MergedCount = Math.Max(1, group.Count);
            consolidated.Add(representative);
        }

        consolidated.AddRange(others);
        return consolidated;
    }

    private static SearchResult? SelectRepresentative(IReadOnlyList<SearchResult> group)
    {
        SearchResult? best = null;
        double bestScore = double.MinValue;

        foreach (var candidate in group)
        {
            var score =
                UrlFormScore(candidate.Url) * 1000 +
                (candidate.IsVerified ? 300 : 0) +
                (candidate.IsOfficial ? 150 : 0) +
                ChannelSubscriberScore(candidate.SubscriberCount) +
                ChannelVideoScore(candidate.VideoCount) +
                ChannelAgeScore(candidate.ChannelPublishedAt) +
                (candidate.ExtraScore ?? 0) * 2 +
                candidate.Confidence * 10;

            if (score > bestScore || (score.Equals(bestScore) && IsBetterMetadata(candidate, best)))
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>Tie-break sur métadonnées entre candidats à score égal : abonnés,
    /// vidéos, ancienneté, vérification, officialité.</summary>
    private static bool IsBetterMetadata(SearchResult candidate, SearchResult? current)
    {
        if (current is null) return true;
        if ((candidate.SubscriberCount ?? 0) != (current.SubscriberCount ?? 0))
            return (candidate.SubscriberCount ?? 0) > (current.SubscriberCount ?? 0);
        if (candidate.IsVerified != current.IsVerified)
            return candidate.IsVerified;
        if (candidate.IsOfficial != current.IsOfficial)
            return candidate.IsOfficial;
        if ((candidate.VideoCount ?? 0) != (current.VideoCount ?? 0))
            return (candidate.VideoCount ?? 0) > (current.VideoCount ?? 0);
        if (candidate.ChannelPublishedAt != current.ChannelPublishedAt)
        {
            var a = candidate.ChannelPublishedAt ?? DateTimeOffset.MaxValue;
            var b = current.ChannelPublishedAt ?? DateTimeOffset.MaxValue;
            return a < b; // plus ancienne = meilleure
        }
        return false;
    }

    private static int UrlFormScore(string url) => HasHandle(url) ? 3 : HasChannelId(url) ? 2 : HasUserPath(url) ? 1 : 0;

    /// <summary>Le handle canonical (@MrBeast) est le meilleur identifiant de chaîne.</summary>
    private static bool HasHandle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!IsYoutubeHost(uri.Host)) return false;
        return uri.AbsolutePath.TrimStart('/').StartsWith('@');
    }

    private static bool HasChannelId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return IsYoutubeHost(uri.Host) && uri.AbsolutePath.TrimStart('/').StartsWith("channel/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasUserPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return IsYoutubeHost(uri.Host) && uri.AbsolutePath.TrimStart('/').StartsWith("user/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsYoutubeHost(string host)
    {
        host = host.ToLowerInvariant();
        return host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal) ||
               host == "youtu.be" || host.EndsWith(".youtu.be", StringComparison.Ordinal);
    }

    /// <summary>
    /// Identité canonique d'une chaîne YouTube : @handle, sinon channel/id,
    /// sinon /user/nom. Deux résultats partageant la MÊME identité sont consolidés.
    /// </summary>
    private static string? ChannelIdentity(SearchResult r)
    {
        if (!Uri.TryCreate(r.Url, UriKind.Absolute, out var uri))
            return null;
        if (!IsYoutubeHost(uri.Host))
            return null;

        var path = uri.AbsolutePath.TrimStart('/'); // e.g. @MrBeast / channel/UC_A / user/Foo
        if (path.StartsWith('@'))
            return "handle:" + path.ToLowerInvariant();
        if (path.StartsWith("channel/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = path.Split('/');
            return parts.Length > 1 ? "channel:" + parts[1].ToLowerInvariant() : null;
        }
        if (path.StartsWith("user/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = path.Split('/');
            return parts.Length > 1 ? "user:" + parts[1].ToLowerInvariant() : null;
        }
        return null;
    }

    private static bool IsYoutubeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return IsYoutubeHost(uri.Host);
    }

    private static double ChannelSubscriberScore(long? subscribers)
        => subscribers is > 0 ? Math.Min(20.0, Math.Log10(subscribers.Value) * 4.0) : 0.0;

    private static double ChannelVideoScore(long? videos)
        => videos is > 0 ? Math.Min(8.0, Math.Log10(videos.Value) * 3.0) : 0.0;

    private static double ChannelAgeScore(DateTimeOffset? created)
    {
        if (created is null || created.Value > DateTimeOffset.UtcNow) return 0.0;
        return Math.Min(10.0, (DateTimeOffset.UtcNow - created.Value).TotalDays / 365.25);
    }
}