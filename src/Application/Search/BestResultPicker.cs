namespace JarvisAI.Application.Search;

/// <summary>
/// Sélectionne UN SEUL résultat à ouvrir selon l'intention : la meilleure chaîne
/// pour une intention "chaîne", la meilleure vidéo pour "vidéo/live/short", sinon
/// le meilleur résultat global. Ne parcourt jamais tous les résultats pour ouvrir
/// tous les liens (garantie "1 requête = 1 ouverture", P19.3).
/// </summary>
public static class BestResultPicker
{
    public static SearchResult? Pick(IReadOnlyList<SearchResult> results, SearchResultType intent)
        => Pick(results, intent, relevanceQuery: null);

    /// <summary>
    /// Sélectionne UN SEUL résultat à ouvrir selon l'intention. Si un
    /// <paramref name="relevanceQuery"/> est fourni, les résultats qui n'ont AUCUN
    /// rapport avec la demande sont écartés : on refuse d'ouvrir un RickRoll au lieu
    /// de ce qui a été demandé. S'il ne reste rien de pertinent, renvoie null.
    /// </summary>
    public static SearchResult? Pick(IReadOnlyList<SearchResult> results, SearchResultType intent, string? relevanceQuery)
    {
        if (results is null || results.Count == 0)
            return null;

        var pool = results;
        if (!string.IsNullOrWhiteSpace(relevanceQuery))
        {
            // Pertinence sur le titre OU l'auteur : un titre de vidéo ne contient
            // pas forcément le sujet demandé, mais son auteur oui (ex. une vidéo de
            // "I Built 100 Houses..." publiée par MrBeast pour la requête "mrbeast").
            var relevant = results
                .Where(r => ResultRelevance.Matches(r.Title, relevanceQuery)
                            || ResultRelevance.Matches(r.Author, relevanceQuery))
                .ToList();
            if (relevant.Count > 0)
                pool = relevant;
            else
                return null; // aucun résultat pertinent : ne PAS ouvrir n'importe quoi.
        }

        var best = pool
            .OrderByDescending(r => r.FinalScore)
            .ThenByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        if (best is null)
            return null;

        if (intent == SearchResultType.Channel)
        {
            var channel = pool
                .Where(r => r.Type == SearchResultType.Channel)
                .OrderByDescending(r => r.FinalScore)
                .ThenByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
            if (channel is not null)
                return channel;
        }
        else if (intent is SearchResultType.Video or SearchResultType.Live or SearchResultType.Short)
        {
            var video = pool
                .Where(r => r.Type is SearchResultType.Video or SearchResultType.Live or SearchResultType.Short)
                .OrderByDescending(r => r.FinalScore)
                .ThenByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
            if (video is not null)
                return video;
        }
        else if (intent == SearchResultType.Playlist)
        {
            var playlist = pool
                .Where(r => r.Type == SearchResultType.Playlist)
                .OrderByDescending(r => r.FinalScore)
                .ThenByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
            if (playlist is not null)
                return playlist;
        }

        return best;
    }
}