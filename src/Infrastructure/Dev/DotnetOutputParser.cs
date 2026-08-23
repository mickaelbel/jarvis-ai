namespace JarvisAI.Infrastructure.Dev;

/// <summary>Un échec de test extrait de la sortie de dotnet test.</summary>
public sealed record TestFailure(string Name, string Message);

/// <summary>Résultat parsé d'une commande dotnet (build ou test).</summary>
public sealed class DotnetResult
{
    public bool Success { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<TestFailure> Failures { get; init; } = Array.Empty<TestFailure>();
}

/// <summary>
/// Parse la sortie console de dotnet build / dotnet test. Tolérant aux sorties
/// FR (« Réussi ! », « échec : 2 ») et EN (« Passed! », « failed: 2 »).
/// </summary>
public static class DotnetOutputParser
{
    public static DotnetResult ParseBuild(string output)
    {
        var errors = new List<string>();
        var warnings = 0;
        foreach (var line in output.Split('\n'))
        {
            var l = line.TrimEnd();
            if (l.Contains(" error ", StringComparison.OrdinalIgnoreCase) || l.Contains(" : erreur ", StringComparison.OrdinalIgnoreCase))
                errors.Add(l.Trim());
            else if (l.Contains(" warning ", StringComparison.OrdinalIgnoreCase) || l.Contains(" : avertissement ", StringComparison.OrdinalIgnoreCase))
                warnings++;
        }
        return new DotnetResult { Success = errors.Count == 0, Errors = errors };
    }

    public static DotnetResult ParseTest(string output)
    {
        var passed = 0;
        var failed = 0;
        var sawSummary = false;
        var failures = new List<TestFailure>();
        var lines = output.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Sommaire uniquement : doit contenir à la fois l'étiquette réussite/passed
            // ET échec/failed (sinon les lignes individuelles « Réussite X [5 ms] »
            // compteraient leur durée comme des tests passés).
            var hasPassedLabel = line.Contains("réussite", StringComparison.OrdinalIgnoreCase)
                                 || line.Contains("passed", StringComparison.OrdinalIgnoreCase);
            var hasFailedLabel = line.Contains("échec", StringComparison.OrdinalIgnoreCase)
                                 || line.Contains("failed", StringComparison.OrdinalIgnoreCase);
            if (hasPassedLabel && hasFailedLabel)
            {
                var p = ExtractNumber(line, "réussite") ?? ExtractNumber(line, "passed");
                var f = ExtractNumber(line, "échec") ?? ExtractNumber(line, "failed");
                if (p.HasValue || f.HasValue)
                {
                    passed += p ?? 0;
                    failed += f ?? 0;
                    sawSummary = true;
                }
            }

            // Lignes d'échec : « Échoué Nom.Test [FAIL] » (FR) ou « Failed Nom.Test [2 s] » (EN)
            var failName = ExtractFailName(line);
            if (!string.IsNullOrEmpty(failName))
            {
                var message = CollectMessage(lines, i + 1);
                failures.Add(new TestFailure(failName, message));
            }
        }

        return new DotnetResult
        {
            Success = sawSummary && failed == 0 && failures.Count == 0,
            Passed = passed,
            Failed = Math.Max(failed, failures.Count),
            Failures = failures
        };
    }

    /// <summary>
    /// Extrait le nom d'un test en échec. Formats : « Échoué Nom.Test [FAIL] » (FR),
    /// « Failed Nom.Test [2 s] » (EN). Retourne vide pour toute autre ligne.
    /// </summary>
    private static string ExtractFailName(string line)
    {
        var t = line.Trim();
        var idx = t.IndexOf("[FAIL]", StringComparison.Ordinal);
        if (idx > 0)
        {
            var before = t[..idx].Trim();
            var parts = before.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts[^1];
        }
        if ((t.StartsWith("Failed ", StringComparison.Ordinal) || t.StartsWith("Échoué ", StringComparison.Ordinal)) &&
            !t.Contains("failed:", StringComparison.OrdinalIgnoreCase) &&
            !t.Contains("échec:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var candidate = parts.Length >= 2 ? parts[1] : "";
            return candidate.Contains('.') ? candidate : "";
        }
        return "";
    }

    private static string CollectMessage(string[] lines, int startIdx)
    {
        // Le bloc « Message d'erreur : » suit immédiatement la ligne [FAIL]
        var collected = new List<string>();
        for (var i = startIdx; i < Math.Min(startIdx + 12, lines.Length); i++)
        {
            var l = lines[i];
            if (l.Contains("Message d'erreur", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("error message", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(l)) break;
            if (l.StartsWith("---") ||
                l.Contains("Arborescence", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("stack trace", StringComparison.OrdinalIgnoreCase)) break;
            collected.Add(l.Trim());
            if (collected.Count >= 4) break;
        }
        return string.Join(" ", collected);
    }

    /// <summary>Trouve le nombre après une étiquette (« réussite: 42 » / « failed: 3 »).</summary>
    public static int? ExtractNumber(string line, string label)
    {
        var idx = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var span = line[(idx + label.Length)..];
        var sb = new System.Text.StringBuilder();
        foreach (var c in span)
        {
            if (char.IsDigit(c)) sb.Append(c);
            else if (sb.Length > 0) break;
        }
        return sb.Length > 0 && int.TryParse(sb.ToString(), out var n) ? n : null;
    }
}