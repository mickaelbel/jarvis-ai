namespace JarvisAI.Application.Voice;

public static class WakeWordMatcher
{
    /// <summary>Hésitations/adresses que le STT colle souvent devant le mot-clé
    /// (« voici Jarvis », « eh ben Jarvis »). Elles sont ignorées si elles ouvrent
    /// l'énoncé ; une vraie phrase (« bonjour jarvis comment ça va ») reste ignorée.</summary>
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "voici", "voila", "eh", "et", "ben", "bin", "bon", "ok", "okay",
        "oui", "hey", "he", "dis", "dit", "allez", "allons", "pardon"
    };

    public static bool TryExtractCommand(string transcript, IReadOnlyList<string> wakePhrases, out string command)
    {
        command = transcript?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command)) return false;

        var words = Tokenize(command);
        if (words.Count == 0) return false;

        // Positions de départ valides : début d'énoncé, ou juste après les
        // hésitations initiales (jamais en plein milieu d'une phrase).
        var afterFillers = 0;
        while (afterFillers < words.Count && afterFillers < 3 && Fillers.Contains(words[afterFillers].Normalized))
            afterFillers++;

        foreach (var rawPhrase in wakePhrases)
        {
            var phraseWords = rawPhrase
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (phraseWords.Length == 0) continue;

            foreach (var startPos in new[] { 0, afterFillers })
            {
                var i = startPos;
                if (i + phraseWords.Length > words.Count) continue;

                var matched = true;
                for (var j = 0; j < phraseWords.Length; j++)
                {
                    if (!WordMatches(words[i + j].Normalized, phraseWords[j]))
                    {
                        matched = false;
                        break;
                    }
                }
                if (!matched) continue;

                var end = words[i + phraseWords.Length - 1].End;
                command = command[Math.Min(end, command.Length)..]
                    .TrimStart(' ', ',', '.', ':', '?', '!', '-', '…');
                return true;
            }
        }

        return false;
    }

    private static List<(string Normalized, int Start, int End)> Tokenize(string text)
    {
        var results = new List<(string, int, int)>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isSep = i == text.Length || char.IsWhiteSpace(text[i]) || IsPunctuation(text[i]);
            if (!isSep && start < 0) start = i;
            if (isSep && start >= 0)
            {
                var raw = text[start..i];
                results.Add((raw.ToLowerInvariant(), start, i));
                start = -1;
            }
        }
        return results;
    }

    private static bool IsPunctuation(char c) =>
        c is ',' or '.' or ':' or ';' or '?' or '!' or '"' or '\'' or '(' or ')' or '…';

    /// <summary>Correspondance tolérante aux erreurs de reconnaissance vocale.</summary>
    private static bool WordMatches(string word, string expected)
    {
        if (word == expected) return true;
        if (string.IsNullOrEmpty(word)) return false;

        // Faute unique admise sur les mots suffisamment longs (« jervis » ≈ « jarvis »),
        // aucune tolérance sur les très courts pour éviter les faux positifs.
        var tolerance = expected.Length >= 5 ? 1 : expected.Length >= 3 ? 1 : 0;
        if (tolerance == 0) return word == expected;
        return LevenshteinDistance(word, expected) <= tolerance;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }
}