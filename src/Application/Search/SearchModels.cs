namespace JarvisAI.Application.Search;

public enum SearchResultType
{
    General,
    OfficialSite,
    Video,
    Channel,
    Playlist,
    Live,
    Short,
    Repository,
    News,
    Academic,
    Local,
    Image
}

public enum LinkStatus
{
    Unknown,
    VerifiedOk,
    Failed,
    Skipped
}

public sealed class SearchRequest
{
    public string Query { get; init; } = "";
    public int MaxResults { get; init; } = 10;
    public string? Language { get; init; }
    public SearchResultType Type { get; init; } = SearchResultType.General;
    public bool VerifyLinks { get; init; } = true;
    public bool PreferOfficial { get; init; } = true;
    public double? NearLatitude { get; init; }
    public double? NearLongitude { get; init; }
    public IReadOnlyList<string>? ProviderNames { get; init; }
}

public sealed class SearchResult
{
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Snippet { get; init; } = "";
    public string Source { get; init; } = "";
    public string Provider { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; init; }
    public SearchResultType Type { get; set; } = SearchResultType.General;
    public bool IsOfficial { get; set; }
    public string? ContentType { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? Author { get; init; }
    public double? ExtraScore { get; init; }

    public double Confidence { get; set; }
    public bool IsVerified { get; set; }
    public LinkStatus LinkStatus { get; set; } = LinkStatus.Unknown;
    public double FinalScore { get; set; }
    public int GroupId { get; set; }
    public int MergedCount { get; set; } = 1;
    public string? VideoId { get; init; }
    public string? ChannelId { get; init; }
    public TimeSpan? Duration { get; init; }
    public bool? OpenNow { get; init; }

    /// <summary>Coordonnées du lieu (résultats géographiques Nominatim, P19.4).</summary>
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    /// <summary>Nombre d'abonnés de la chaîne (résultats YouTube).</summary>
    public long? SubscriberCount { get; init; }

    /// <summary>Nombre de vidéos publiées par la chaîne (résultats YouTube).</summary>
    public long? VideoCount { get; init; }

    /// <summary>Date de création de la chaîne (ancienneté, résultats YouTube).</summary>
    public DateTimeOffset? ChannelPublishedAt { get; init; }

    /// <summary>
    /// Accord multi-providers sur l'entité (0..1) : part des providers ayant
    /// renvoyé cette entité sur le total consulté. Positionné par EntityConsolidator.
    /// </summary>
    public double? EntityAgreement { get; set; }

    /// <summary>
    /// Décompte du nombre d'OPEN distincts déjà réalisés pour cette entité dans la
    /// session courante (anti-doublon d'onglets, P19.3).
    /// </summary>
    public int EntityOpenCount { get; set; }
}

public sealed class SearchResponse
{
    public string Query { get; init; } = "";
    public IReadOnlyList<SearchResult> Results { get; init; } = Array.Empty<SearchResult>();
    public IReadOnlyList<string> ProvidersUsed { get; init; } = Array.Empty<string>();
    public bool FromCache { get; set; }
    public DateTimeOffset CompletedAt { get; init; }
    public int TotalProviderResults { get; init; }
    public int MergedCount { get; init; }
    public int TotalGroups { get; init; }
    public double ConsensusAgreement { get; init; }
    public string? WinnerProvider { get; init; }
    public IReadOnlyList<ProviderCallResult> ProviderStats { get; init; } = Array.Empty<ProviderCallResult>();
}

public sealed record ProviderCallResult(string Provider, bool Succeeded, long ElapsedMs, int ResultCount);

public sealed record ProviderHealth(int Calls, int Failures, long TotalMs);

public sealed class OfficialSiteMatch
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public double Confidence { get; init; }
    public string MatchKind { get; init; } = "";
}
