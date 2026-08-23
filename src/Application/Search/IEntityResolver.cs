namespace JarvisAI.Application.Search;

/// <summary>
/// Résout une entité (marque, créateur, projet, école…) vers sa ressource OFFICIELLE :
/// chaîne YouTube, dépôt GitHub, site web ou documentation — au lieu de rechercher
/// puis d'ouvrir le premier résultat.
/// </summary>
public interface IEntityResolver
{
    /// <summary>Résolution "meilleure" multi-types (URL directe, école, chaîne, repo, site, docs, créateur).</summary>
    Task<EntityResolution?> ResolveOfficialAsync(string entity, CancellationToken cancellationToken = default);

    /// <summary>Chaîne YouTube officielle d'une entité (ex. "MrBeast" → youtube.com/@MrBeast).</summary>
    OfficialSiteMatch? ResolveOfficialYouTubeChannel(string entity);

    /// <summary>Dépôt GitHub officiel d'une entité (ex. "ollama" → github.com/ollama/ollama).</summary>
    OfficialSiteMatch? ResolveOfficialGitHub(string entity);

    /// <summary>Site web officiel d'une entité (ex. "netflix" → netflix.com).</summary>
    OfficialSiteMatch? ResolveOfficialWebsite(string entity);

    /// <summary>Documentation officielle d'une entité (ex. "python" → docs.python.org).</summary>
    OfficialSiteMatch? ResolveOfficialDocumentation(string entity);
}

/// <summary>Résultat de résolution d'entité officielle.</summary>
public sealed record EntityResolution(string Entity, string Kind, string Url, double Confidence, string? VerifiedSource);
