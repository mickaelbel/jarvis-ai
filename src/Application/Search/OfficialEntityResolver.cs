namespace JarvisAI.Application.Search;

/// <summary>
/// Implémentation par défaut de <see cref="IEntityResolver"/> basée sur le
/// <see cref="OfficialSiteCatalog"/> (domaines, docs, repos, chaînes, écoles).
/// </summary>
public sealed class OfficialEntityResolver : IEntityResolver
{
    private readonly OfficialSiteDetector _detector = new();

    public Task<EntityResolution?> ResolveOfficialAsync(string entity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entity))
            return Task.FromResult<EntityResolution?>(null);

        var match = _detector.ResolveOfficial(entity.Trim());
        if (match is null)
            return Task.FromResult<EntityResolution?>(null);

        return Task.FromResult<EntityResolution?>(
            new EntityResolution(match.Name, match.MatchKind, match.Url, match.Confidence, null));
    }

    public OfficialSiteMatch? ResolveOfficialYouTubeChannel(string entity)
    {
        if (string.IsNullOrWhiteSpace(entity))
            return null;
        foreach (var key in OfficialSiteCatalog.ChannelKeys.OrderByDescending(k => k.Length))
        {
            if (OfficialSiteDetector.ContainsWord(entity, key) &&
                OfficialSiteCatalog.TryGetOfficialChannel(key, out var url))
            {
                return new OfficialSiteMatch { Name = key, Url = url, Confidence = 0.98, MatchKind = "youtube-channel" };
            }
        }
        return null;
    }

    public OfficialSiteMatch? ResolveOfficialGitHub(string entity)
    {
        if (string.IsNullOrWhiteSpace(entity))
            return null;
        foreach (var key in OfficialSiteCatalog.RepoKeys.OrderByDescending(k => k.Length))
        {
            if (OfficialSiteDetector.ContainsWord(entity, key) &&
                OfficialSiteCatalog.TryGetOfficialRepo(key, out var repo))
            {
                return new OfficialSiteMatch { Name = key, Url = $"https://github.com/{repo}", Confidence = 0.99, MatchKind = "github-repo" };
            }
        }
        return null;
    }

    public OfficialSiteMatch? ResolveOfficialWebsite(string entity)
    {
        if (string.IsNullOrWhiteSpace(entity))
            return null;
        foreach (var key in OfficialSiteCatalog.DomainKeys.OrderByDescending(k => k.Length))
        {
            if (OfficialSiteDetector.ContainsWord(entity, key) &&
                OfficialSiteCatalog.TryGetOfficialDomain(key, out var domain))
            {
                return new OfficialSiteMatch { Name = key, Url = $"https://{domain}", Confidence = 0.97, MatchKind = "official-domain" };
            }
        }
        return null;
    }

    public OfficialSiteMatch? ResolveOfficialDocumentation(string entity)
    {
        if (string.IsNullOrWhiteSpace(entity))
            return null;
        if (OfficialSiteCatalog.TryGetOfficialDocsUrl(entity.Trim(), out var url))
            return new OfficialSiteMatch { Name = entity.Trim(), Url = url, Confidence = 0.98, MatchKind = "docs" };
        return null;
    }
}
