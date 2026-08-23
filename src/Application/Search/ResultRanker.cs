namespace JarvisAI.Application.Search;

public sealed class ResultRanker
{
    private const double OfficialBonus = 0.22;
    private const double VerifiedBonus = 0.10;
    private const double MediaBonus = 0.10;
    private const double DocsBonus = 0.10;
    private const double PreferOfficialExtra = 0.05;
    private const double MaxRecencyBonus = 4.0;
    private const double MaxRelevanceBonus = 12.0;
    private const double OfficialRepoBonus = 15.0;
    private const double OfficialChannelBonus = 18.0;
    private const double SchoolBonus = 12.0;
    private const double ConstructionBonus = 12.0;
    private const double ChannelVerifiedBonus = 12.0;
    private const double MaxChannelSubscriberBonus = 20.0;
    private const double MaxChannelVideoBonus = 8.0;
    private const double MaxChannelAgeBonus = 10.0;
    private const double EntityAgreementBonus = 20.0;

    public IReadOnlyList<SearchResult> Rank(
        IEnumerable<SearchResult> results,
        string? query = null,
        SearchResultType requestType = SearchResultType.General,
        bool preferOfficial = true,
        FakeSiteDetector? fakeSiteDetector = null)
    {
        var list = results.ToList();
        foreach (var result in list)
        {
            var score = result.Confidence * 100;
            if (result.ExtraScore is { } relevance)
                score += relevance * 0.4;

            if (result.IsOfficial)
                score += (OfficialBonus + (preferOfficial ? PreferOfficialExtra : 0)) * 100;
            if (result.IsVerified)
                score += VerifiedBonus * 100;

            if (result.Type == SearchResultType.OfficialSite)
                score += 15;

            var host = SearchResultDeduper.CanonicalDomain(result.Url);
            if (OfficialSiteCatalog.IsMediaDomain(host))
                score += MediaBonus * 100;
            if (OfficialSiteCatalog.IsDocsHost(host) || result.ContentType == "docs")
                score += DocsBonus * 100;

            if (!string.IsNullOrWhiteSpace(query))
                score += RelevanceScorer.Score(query, result.Title, result.Snippet) * MaxRelevanceBonus;

            score += RecencyBonus(result.PublishedAt);

            if (result.ContentType is "pdf" || result.Type == SearchResultType.Academic)
                score += 20;
            if (result.Type is SearchResultType.Video or SearchResultType.Live or SearchResultType.Short)
                score += 10;
            if (result.ContentType is "video" && result.Type is SearchResultType.Video or SearchResultType.Live or SearchResultType.Short)
                score += 5;

            // Boost fort pour les dépôts GitHub / chaînes YouTube officiellement reconnus
            if (result.Type == SearchResultType.Repository && result.IsOfficial)
                score += OfficialRepoBonus;
            if (result.Type == SearchResultType.Channel && result.IsOfficial)
                score += OfficialChannelBonus;

            // Qualité d'une chaîne YouTube : vérifiée, abonnés, vidéos, ancienneté.
            if (result.Type == SearchResultType.Channel)
            {
                if (result.IsVerified)
                    score += ChannelVerifiedBonus;
                score += ChannelSubscriberBonus(result.SubscriberCount);
                score += ChannelVideoBonus(result.VideoCount);
                score += ChannelAgeBonus(result.ChannelPublishedAt);
                score += EntityAgreementTotalBoost(result.EntityAgreement);
            }

            // Boost pour les écoles / pages constructeur officielles
            if (Uri.TryCreate(result.Url, UriKind.Absolute, out var rankedUri))
            {
                var h = rankedUri.Host.ToLowerInvariant();
                if (h.StartsWith("www.", StringComparison.Ordinal)) h = h[4..];
                if (IsSchoolLike(h))
                    score += SchoolBonus;
                if (IsConstructionLike(h) && result.IsOfficial)
                    score += ConstructionBonus;
            }

            if (fakeSiteDetector is not null && Uri.TryCreate(result.Url, UriKind.Absolute, out var uri))
            {
                var trust = fakeSiteDetector.ComputeTrustScore(uri);
                if (trust < 0.5)
                    score -= 50;
                else if (trust < 0.8)
                    score -= 10;
            }

            result.FinalScore = Math.Max(0, score);
        }

        return list.OrderByDescending(r => r.FinalScore)
            .ThenByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    /// <summary>Bonus d'accord multi-providers : l'entité confirmée par plusieurs
    /// sources est fiable (cohérence cross-provider).</summary>
    private static double EntityAgreementTotalBoost(double? agreement)
        => Math.Max(0.0, Math.Min(1.0, agreement ?? 0.0)) * EntityAgreementBonus;

    private static bool IsSchoolLike(string host)
        => host.StartsWith("ac-", StringComparison.Ordinal) ||
           host.Equals("onisep.fr", StringComparison.Ordinal) ||
           host.Equals("parcoursup.fr", StringComparison.Ordinal) ||
           host.Equals("letudiant.fr", StringComparison.Ordinal) ||
           host.Equals("studyrama.com", StringComparison.Ordinal);

    private static bool IsConstructionLike(string host)
    {
        var constructions = new[] {
            "apple.com", "microsoft.com", "samsung.com", "sony.com", "nintendo.com",
            "playstation.com", "xbox.com", "intel.com", "nvidia.com", "amd.com",
            "tesla.com", "bmw.com", "mercedes-benz.com", "audi.com", "toyota.com",
            "honda.com", "volvo.com", "dell.com", "hp.com", "lenovo.com", "asus.com",
            "acer.com", "msi.com", "logitech.com", "razer.com", "corsair.com"
        };
        foreach (var c in constructions)
            if (host == c || host.EndsWith("." + c, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static double RecencyBonus(DateTimeOffset? published)
    {
        if (published is null)
            return 0.0;
        var age = DateTimeOffset.UtcNow - published.Value;
        if (age < TimeSpan.Zero)
            return MaxRecencyBonus;
        var days = age.TotalDays;
        if (days > 365)
            return 0.0;
        return Math.Round(MaxRecencyBonus * (1.0 - days / 365.0), 2);
    }

    /// <summary>
    /// Bonus logarithmique d'abonnés : une chaîne de 2 100 abonnés gagne ~14 pts,
    /// une chaîne de 400 M d'abonnés atteint le plafond de 20 pts.
    /// </summary>
    private static double ChannelSubscriberBonus(long? subscribers)
        => subscribers is > 0
            ? Math.Min(MaxChannelSubscriberBonus, Math.Log10(subscribers.Value) * 4.0)
            : 0.0;

    /// <summary>Bonus logarithmique du nombre de vidéos publiées (plafonné à 8 pts).</summary>
    private static double ChannelVideoBonus(long? videos)
        => videos is > 0
            ? Math.Min(MaxChannelVideoBonus, Math.Log10(videos.Value) * 3.0)
            : 0.0;

    /// <summary>Bonus d'ancienneté de la chaîne : 1 pt par année, plafonné à 10 pts.</summary>
    private static double ChannelAgeBonus(DateTimeOffset? created)
    {
        if (created is null || created.Value > DateTimeOffset.UtcNow)
            return 0.0;
        var years = (DateTimeOffset.UtcNow - created.Value).TotalDays / 365.25;
        return Math.Round(Math.Min(MaxChannelAgeBonus, years), 2);
    }
}
