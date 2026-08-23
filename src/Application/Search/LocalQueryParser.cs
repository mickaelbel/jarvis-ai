using System.Text.RegularExpressions;

namespace JarvisAI.Application.Search;

public sealed class LocalQuery
{
    public string? Category { get; init; }
    public string? Cuisine { get; init; }
    public string? City { get; init; }
    public bool OpenNow { get; init; }
    public bool TopRated { get; init; }

    /// <summary>
    /// L'utilisateur demande un lieu "près de moi / chez moi" sans coordonnées :
    /// le provider doit alors partir d'une localisation courante plutôt qu'une ville.
    /// </summary>
    public bool NearLocation { get; init; }
    public string CleanQuery { get; init; } = "";
}

public static class LocalQueryParser
{
    private static readonly string[] NearMarkers =
    {
        "près de", "pres de", "à côté de", "a cote de", "autour de", "proche de",
        "près d'", "pres d'", "autour d'", "proche d'", "à proximité de", "a proximite de",
        "près de chez", "pres de chez", "près de moi", "pres de moi", "pas loin de", "non loin de"
    };

    private static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        // Scolarité - prépas / grandes écoles
        ["prépa intégrée"] = "école d'ingénieurs",
        ["prepa integree"] = "école d'ingénieurs",
        ["prépa intégrée"] = "école d'ingénieurs",
        ["cpge intégrée"] = "école d'ingénieurs",
        ["cpge integree"] = "école d'ingénieurs",
        ["école d'ingénieurs"] = "école d'ingénieurs",
        ["école d'ingénieur"] = "école d'ingénieurs",
        ["ecole d'ingenieurs"] = "école d'ingénieurs",
        ["ecole d'ingenieur"] = "école d'ingénieurs",
        ["ecole d ingénieurs"] = "école d'ingénieurs",
        ["ecole d ingenieurs"] = "école d'ingénieurs",
        ["école d ingénieurs"] = "école d'ingénieurs",
        ["école d ingenieurs"] = "école d'ingénieurs",
        ["école de commerce"] = "école de commerce",
        ["ecole de commerce"] = "école de commerce",
        ["business school"] = "école de commerce",
        ["ingenieur"] = "école d'ingénieurs",
        ["ingénieur"] = "école d'ingénieurs",
        ["ingénieurs"] = "école d'ingénieurs",
        ["ingenieurs"] = "école d'ingénieurs",
        ["cpge"] = "prépa",
        ["sti2d"] = "prépa",
        ["bts"] = "bts",
        ["dut"] = "dut",
        ["licence"] = "licence",
        ["master"] = "master",
        ["prépa"] = "prépa",
        ["prépas"] = "prépa",
        ["prepa"] = "prépa",
        ["prepas"] = "prépa",
        ["lycée"] = "lycée",
        ["lycee"] = "lycée",
        ["lycées"] = "lycée",
        ["lycees"] = "lycée",
        ["collège"] = "collège",
        ["college"] = "collège",
        ["école"] = "école",
        ["ecole"] = "école",
        ["université"] = "université",
        ["universite"] = "université",
        ["fac"] = "université",
        ["garage"] = "garage",
        ["garages"] = "garage",
        ["restaurant"] = "restaurant",
        ["restaurants"] = "restaurant",
        ["pizzeria"] = "pizzeria",
        ["coiffeur"] = "coiffeur",
        ["coiffure"] = "coiffeur",
        ["pharmacie"] = "pharmacie",
        ["boulangerie"] = "boulangerie",
        ["boulanger"] = "boulangerie",
        ["boucherie"] = "boucherie",
        ["super marché"] = "supermarché",
        ["supermarché"] = "supermarché",
        ["supermarche"] = "supermarché",
        ["hôtel"] = "hôtel",
        ["hotel"] = "hôtel",
        ["cinéma"] = "cinéma",
        ["cinema"] = "cinéma",
        ["médecin"] = "médecin",
        ["medecin"] = "médecin",
        ["dentiste"] = "dentiste",
        ["avocat"] = "avocat",
        ["notaire"] = "notaire",
        ["banque"] = "banque",
        ["bibliothèque"] = "bibliothèque",
        ["bibliotheque"] = "bibliothèque",
        ["salle de sport"] = "salle de sport",
        ["gym"] = "salle de sport",
        ["mécanicien"] = "garage",
        ["mecanicien"] = "garage",
        ["vétérinaire"] = "vétérinaire",
        ["veterinaire"] = "vétérinaire"
    };

    private static readonly Dictionary<string, string> Cuisines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["italien"] = "italien",
        ["italienne"] = "italien",
        ["japonais"] = "japonais",
        ["japonaise"] = "japonais",
        ["chinois"] = "chinois",
        ["chinoise"] = "chinois",
        ["thaï"] = "thaï",
        ["thai"] = "thaï",
        ["libanais"] = "libanais",
        ["libanaise"] = "libanais",
        ["marocain"] = "marocain",
        ["marocaine"] = "marocain",
        ["indien"] = "indien",
        ["indienne"] = "indien",
        ["mexicain"] = "mexicain",
        ["mexicaine"] = "mexicain",
        ["coréen"] = "coréen",
        ["coreen"] = "coréen",
        ["vietnamien"] = "vietnamien",
        ["vietnamienne"] = "vietnamien",
        ["américain"] = "américain",
        ["americain"] = "américain",
        ["grec"] = "grec",
        ["grecque"] = "grec",
        ["espagnol"] = "espagnol",
        ["espagnole"] = "espagnol",
        ["portugais"] = "portugais",
        ["portugaise"] = "portugais",
        ["pizza"] = "italien",
        ["sushi"] = "japonais",
        ["kebab"] = "kebab"
    };

    public static LocalQuery Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new LocalQuery();

        var text = Regex.Replace(query.Trim(), @"\s+", " ");
        var lower = " " + text.ToLowerInvariant() + " ";

        var openNow = lower.Contains(" ouvert maintenant") ||
                      lower.Contains(" ouvert ") ||
                      lower.Contains(" open now") ||
                      lower.Contains(" ouverture");
        var topRated = lower.Contains(" meilleur") ||
                       lower.Contains(" meilleurs") ||
                       lower.Contains(" meilleure") ||
                       lower.Contains(" meilleures") ||
                       lower.Contains(" top") ||
                       lower.Contains(" le mieux");
        var nearLocation = lower.Contains(" près de moi") ||
                           lower.Contains(" pres de moi") ||
                           lower.Contains(" près de chez moi") ||
                           lower.Contains(" pres de chez moi") ||
                           lower.Contains(" autour de moi") ||
                           lower.Contains(" chez moi") ||
                           lower.Contains(" pas loin d'ici") ||
                           lower.Contains(" non loin d'ici");
        var leMeilleur = lower.Contains(" le meilleur");
        var laMeilleure = lower.Contains(" la meilleure");

        string? category = null;
        string? cuisine = null;
        foreach (var (key, value) in Categories.OrderByDescending(kv => kv.Key.Length))
        {
            if (Regex.IsMatch(lower, @"\b" + Regex.Escape(key) + @"s?\b"))
            {
                category = value;
                break;
            }
        }
        foreach (var (key, value) in Cuisines.OrderByDescending(kv => kv.Key.Length))
        {
            if (Regex.IsMatch(lower, @"\b" + Regex.Escape(key) + @"\b"))
            {
                cuisine = value;
                if (category is null && value == "italien")
                    category = "restaurant";
                break;
            }
        }

        var city = ExtractCity(text);

        var clean = CleanQuery(text, city, topRated, leMeilleur, laMeilleure, openNow);

        return new LocalQuery
        {
            Category = category,
            Cuisine = cuisine,
            City = city,
            OpenNow = openNow,
            TopRated = topRated,
            NearLocation = nearLocation,
            CleanQuery = clean
        };
    }

    private static string? ExtractCity(string text)
    {
        var idx = text.IndexOf(":", StringComparison.Ordinal);
        if (idx >= 0)
            text = text[idx..];

        var lower = " " + text.ToLowerInvariant() + " ";
        foreach (var marker in NearMarkers.OrderByDescending(m => m.Length))
        {
            var at = lower.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                continue;
            // 'lower' porte un espace préfixé : l'index réel dans 'text' est at - 1.
            var textPos = Math.Max(0, at - 1);
            var rest = text[(textPos + marker.Length)..].Trim().TrimStart(' ', '\'', '«', '"', '(');
            var parts = rest.Split(new[] { " et ", " ou " }, StringSplitOptions.RemoveEmptyEntries);
            rest = (parts.Length > 0 ? parts[0] : rest).Trim();
            // Le marqueur court "près de" peut laisser "de moi" : on retire la
            // préposition avant de tester la position courante.
            rest = StripLeadingPreposition(rest);
            // "près de moi/chez moi" = position courante, pas une ville.
            if (string.Equals(rest, "moi", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rest, "chez moi", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rest, "ici", StringComparison.OrdinalIgnoreCase))
                return null;
            if (rest.Length >= 2 && rest.Length <= 40)
                return rest!;
        }

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = tokens.Length - 1; i >= 0; i--)
        {
            var token = tokens[i].Trim(' ', ',', '.', '!', '?', '"', '\'', '»', '«');
            if (token.Length >= 3 && char.IsUpper(token[0]) &&
                !token.Equals("France", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("Moi", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("Quartier", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("Centre", StringComparison.OrdinalIgnoreCase))
            {
                return token;
            }
        }
        return null;
    }

    private static string StripLeadingPreposition(string rest)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var p in new[] { "de la ", "de l'", "de le ", "des ", "du ", "de ", "d'", "la ", "le ", "les " })
            {
                if (rest.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    rest = rest[p.Length..].Trim();
                    changed = true;
                    break;
                }
            }
        }
        return rest;
    }

    private static string CleanQuery(string text, string? city, bool topRated, bool leMeilleur, bool laMeilleure, bool openNow)
    {
        var clean = text;
        if (!string.IsNullOrWhiteSpace(city))
        {
            foreach (var marker in NearMarkers)
            {
                var at = clean.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    clean = clean[..at];
                    break;
                }
            }
            var cityAt = clean.IndexOf(city, StringComparison.OrdinalIgnoreCase);
            if (cityAt >= 0)
                clean = clean[..cityAt];
        }

        var tokens = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Equals("de", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("du", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("des", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("la", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("le", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("les", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("un", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("une", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("pour", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("près", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("pres", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("autour", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("proche", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("à", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("top", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("meilleur", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("meilleurs", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("meilleure", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("meilleures", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("ouvert", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("maintenant", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("mieux", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("dans", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("sur", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("moi", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("chez", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("ici", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("pas", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("loin", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("non", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var joined = string.Join(" ", tokens);
        if (string.IsNullOrWhiteSpace(joined))
            return (topRated ? "meilleur " : "") + (city ?? "");
        return joined;
    }
}
