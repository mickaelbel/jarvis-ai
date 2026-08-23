namespace JarvisAI.Application.Search;

public enum MapsIntentKind
{
    None = 0,

    /// <summary>Recherche d'un lieu / adresse sur la carte (mode par défaut).</summary>
    Place = 1,

    /// <summary>Itinéraire d'un point à un autre.</summary>
    Directions = 2
}

public sealed record MapsIntent(
    MapsIntentKind Kind,
    string? Destination,
    string? Origin,
    string SearchText);

/// <summary>
/// P19.4 — Interprète les demandes de cartographie : "ouvre la carte de X",
/// "adresse 12 rue de la paix", "itinéraire vers la gare de Bordeaux depuis Mérignac",
/// "route from Paris to Lyon". Le texte du lieu reste lisible tel quel pour le
/// moteur de carte (Google Maps), seuls les marqueurs sont retirés.
/// </summary>
public static class MapsIntentParser
{
    private static readonly string[] DirectionMarkers =
    {
        "comment aller à", "comment aller au", "comment aller jusqu'à", "how do i get to",
        "itinéraire pour aller à", "itineraire pour aller à", "itinéraire jusqu'à", "itineraire jusqu'à",
        "itinéraire vers", "itineraire vers", "itinéraire pour", "itineraire pour", "itinéraire",
        "itineraire", "trajet vers", "trajet pour", "trajet", "route vers", "route pour",
        "route from", "route to", "directions to", "directions pour", "navigate to", "get to", "going to",
        "se rendre jusqu'à", "se rendre à", "se rendre au", "aller jusqu'à", "aller à", "aller au",
        "jusqu'à", "jusqu a"
    };

    private static readonly string[] OriginMarkers =
    {
        "partant de", "au départ de", "au depart de", "à partir de", "a partir de", "starting from",
        "depuis", "de chez", "from"
    };

    public static MapsIntent Parse(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance))
            return new MapsIntent(MapsIntentKind.None, null, null, "");

        var text = utterance.Trim();
        var lower = " " + text.ToLowerInvariant() + " ";

        var marker = FindLongest(lower, DirectionMarkers);
        if (marker is null)
            return new MapsIntent(MapsIntentKind.Place, null, null, text);

        var (index, markerText) = marker.Value;
        // 'lower' porte un espace préfixé : l'index réel dans 'text' est index - 1.
        var textPos = Math.Max(0, index - 1);
        var after = text[(textPos + markerText.Length)..].Trim();
        var origin = ExtractOrigin(text, textPos);

        after = StripLeading(after, new[] { " ", "-", ":", "à", "À", "chez", "Chez", "vers", "Vers", "pour", "Pour" });

        // Origine exprimée APRÈS la destination : "itinéraire vers la gare depuis Mérignac",
        // "route from Paris to Lyon".
        var split = SplitAfterDestination(ref after);
        if (split is not null && origin is null)
            origin = split;
        else if (split is not null)
            after = after[(split.Length + 1)..].Trim();

        after = after.Split(new[] { " et ", " ou ", " et/ou " }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        if (after.Length == 0)
            return new MapsIntent(MapsIntentKind.None, null, null, text);

        return new MapsIntent(MapsIntentKind.Directions, after, origin, text);
    }

    /// <summary>
    /// Si la destination cumulée contient un second lieu (origine) — "la gare depuis
    /// Mérignac", "Paris to Lyon" — on le détache. Renvoie l'origine ou null.
    /// </summary>
    private static string? SplitAfterDestination(ref string after)
    {
        foreach (var separator in new[] { " depuis ", " partant de ", " au départ de ", " de chez " })
        {
            var idx = after.IndexOf(" " + separator.Trim() + " ", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                var origin = after[(idx + separator.Length)..].Trim();
                after = after[..idx].Trim();
                return origin.Length >= 2 ? origin : null;
            }
        }
        var toIdx = after.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (toIdx > 0)
        {
            var origin = after[(toIdx + 4)..].Trim();
            after = after[..toIdx].Trim();
            return origin.Length >= 2 ? origin : null;
        }
        return null;
    }

    private static string? ExtractOrigin(string text, int markerStart)
    {
        var before = text[..markerStart];
        var lower = " " + before.ToLowerInvariant() + " ";
        foreach (var marker in OriginMarkers.OrderByDescending(m => m.Length))
        {
            var at = lower.LastIndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                continue;
            // 'lower' porte un espace préfixé : l'index réel dans 'before' est at - 1.
            var beforePos = Math.Max(0, at - 1);
            var origin = StripLeading(before[(beforePos + marker.Length)..].Trim(),
                new[] { " ", "-", ":", "la", "le", "les", "La", "Le", "Les" });
            if (origin.Length >= 2)
                return origin;
        }
        return null;
    }

    /// <summary>
    /// Retire itérativement en début de chaîne tous les préfixes fournis (mots ou
    /// caractères), jusqu'à ce qu'aucun ne corresponde plus. Gère les déterminants
    /// français ("la", "le", "les") que TrimStart(char[]) ne sait pas traiter.
    /// </summary>
    private static string StripLeading(string text, string[] prefixes)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var p in prefixes)
            {
                if (text.StartsWith(p, StringComparison.Ordinal))
                {
                    text = text[p.Length..].TrimStart(' ');
                    changed = true;
                    break;
                }
            }
        }
        return text;
    }

    private static (int Index, string Marker)? FindLongest(string lower, string[] markers)
    {
        (int Index, string Marker)? best = null;
        foreach (var marker in markers)
        {
            var at = lower.IndexOf(" " + marker + " ", StringComparison.Ordinal);
            if (at < 0)
            {
                at = lower.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0)
                    continue;
            }
            if (best is null || at < best.Value.Index)
                best = (at, marker);
        }
        return best;
    }
}
