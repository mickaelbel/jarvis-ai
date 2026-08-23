using System.Text.RegularExpressions;

namespace JarvisAI.Application.Voice;

/// <summary>
/// Normalisations de prononciation appliquées au texte avant synthèse vocale.
/// </summary>
public static class TtsPronunciation
{
    private static readonly Regex JarvisRegex = new(@"\bjarvis\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Normalize(string text, string voice)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (!IsFrench(voice)) return text;

        // En français, le "s" final de "jarvis" est muet. "jarvisse" force
        // espeak/piper à prononcer le son /s/ (le "e" final est muet).
        return JarvisRegex.Replace(text, m => m.Value[0] == 'J' ? "Jarvisse" : "jarvisse");
    }

    private static bool IsFrench(string voice)
    {
        if (string.IsNullOrWhiteSpace(voice)) return true; // voix par défaut : piper français (fr_FR-*)

        var v = voice.ToLowerInvariant();

        // Piper : "fr_FR-...", "fr-fr-..." ; Windows : "fr-FR", "fr_CA", etc.
        if (v.StartsWith("fr", StringComparison.Ordinal))
            return true;
        if (v.Contains("french", StringComparison.Ordinal)
            || v.Contains("français", StringComparison.Ordinal)
            || v.Contains("francais", StringComparison.Ordinal)
            || v.Contains("-fr", StringComparison.Ordinal)
            || v.Contains("_fr", StringComparison.Ordinal)
            || v.Contains("(fr", StringComparison.Ordinal))
            return true;

        // Voix Windows SAPI françaises courantes (le nom ne contient pas toujours "fr").
        return v.Contains("hortense", StringComparison.Ordinal)
            || v.Contains("hélène", StringComparison.Ordinal)
            || v.Contains("helene", StringComparison.Ordinal)
            || v.Contains("julie", StringComparison.Ordinal)
            || v.Contains("denise", StringComparison.Ordinal);
    }
}
