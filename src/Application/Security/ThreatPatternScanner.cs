using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Security;

/// <summary>
/// Scanne les entrées utilisateur et les résultats d'outils pour détecter
/// les tentatives d'injection de prompt. Protège contre les attaques par
/// prompt injection, role-play hijack, et injection indirecte via des fichiers.
/// </summary>
public sealed class ThreatPatternScanner
{
    private readonly ILogger<ThreatPatternScanner> _logger;

    // Patterns d'injection connus
    private static readonly (string Pattern, string Description, ThreatLevel Level)[] InjectionPatterns = new[]
    {
        // Injection directe
        (@"(?i)ignore\s+(all\s+)?(previous|prior|above|earlier)\s+(instructions?|prompts?|rules?)", "Tentative d'ignorer les instructions", ThreatLevel.High),
        (@"(?i)you\s+are\s+now\s+(a|an|the)\s+\w+", "Tentative de changement de rôle", ThreatLevel.Medium),
        (@"(?i)disregard\s+(all|any|everything)", "Tentative d'ignorer le contexte", ThreatLevel.High),
        (@"(?i)new\s+instructions?\s*:", "Instructions de remplacement détectées", ThreatLevel.High),
        (@"(?i)system\s*prompt\s*:", "Tentative d'injection system prompt", ThreatLevel.High),
        (@"(?i)override\s+(safety|security|rules?)", "Tentative de contournement sécurité", ThreatLevel.Critical),

        // Role-play hijack
        (@"(?i)DAN\s+mode", "Tentative de jailbreak DAN", ThreatLevel.High),
        (@"(?i)do\s+anything\s+now", "Jailbreak DAN détecté", ThreatLevel.High),
        (@"(?i)pretend\s+(you|that)\s+(are|you're)\s+not", "Tentative de faire croire à l'absence de restrictions", ThreatLevel.Medium),
        (@"(?i)act\s+as\s+if\s+you\s+have\s+no\s+restrictions", "Tentative de contournement de restrictions", ThreatLevel.High),

        // Injection indirecte (via fichiers)
        (@"(?i)<\|im_start\|>", "Injection de tokens de chat spéciaux", ThreatLevel.Critical),
        (@"(?i)<\|im_end\|>", "Injection de tokens de chat spéciaux", ThreatLevel.Critical),
        (@"(?i)\[INST\]", "Injection de format Llama", ThreatLevel.Critical),
        (@"(?i)<<SYS>>", "Injection de système Llama", ThreatLevel.Critical),
        (@"(?i)Human:", "Injection de rôle Human", ThreatLevel.Medium),
        (@"(?i)Assistant:", "Injection de rôle Assistant", ThreatLevel.Medium),
        (@"(?i)###\s*(System|Instruction)", "Injection de format markdown", ThreatLevel.Medium),

        // Exfiltration de données
        (@"(?i)send\s+(all\s+)?(data|information|content)\s+to\s+http", "Tentative d'exfiltration de données", ThreatLevel.Critical),
        (@"(?i)upload\s+(to|everything)\s+https?://", "Tentative d'exfiltration", ThreatLevel.Critical),
        (@"(?i)POST\s+https?://.*\b(api|webhook|server)\b", "Envoi de données vers un serveur externe", ThreatLevel.High),

        // Manipulation de tool results
        (@"(?i)tool\s+result\s*:\s*success", "Tentative de falsification de résultat d'outil", ThreatLevel.High),
        (@"(?i)\[tool_output\]\s*(error|success|output)", "Injection de format tool output", ThreatLevel.High),
    };

    public ThreatPatternScanner(ILogger<ThreatPatternScanner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Scanne un texte et retourne les menaces détectées.
    /// </summary>
    public IReadOnlyList<ThreatDetection> Scan(string text, string source = "unknown")
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<ThreatDetection>();

        var detections = new List<ThreatDetection>();

        foreach (var (pattern, description, level) in InjectionPatterns)
        {
            if (Regex.IsMatch(text, pattern))
            {
                var detection = new ThreatDetection
                {
                    Pattern = pattern,
                    Description = description,
                    Level = level,
                    Source = source,
                    DetectedAt = DateTime.UtcNow,
                    MatchedText = ExtractMatch(text, pattern)
                };
                detections.Add(detection);

                _logger.LogWarning("[ThreatScan] {Level} détecté dans {Source}: {Description} (match: \"{Match}\")",
                    level, source, description, detection.MatchedText);
            }
        }

        return detections.AsReadOnly();
    }

    /// <summary>
    /// Vérifie si un texte est sûr (pas de menaces critiques).
    /// </summary>
    public bool IsSafe(string text, string source = "unknown")
    {
        var threats = Scan(text, source);
        return !threats.Any(t => t.Level >= ThreatLevel.High);
    }

    /// <summary>
    /// Scanne et nettoie un texte en supprimant les patterns dangereux.
    /// </summary>
    public (string CleanedText, IReadOnlyList<ThreatDetection> Threats) Sanitize(string text, string source = "unknown")
    {
        var threats = Scan(text, source);
        var cleaned = text;

        foreach (var threat in threats.Where(t => t.Level >= ThreatLevel.High))
        {
            cleaned = Regex.Replace(cleaned, threat.Pattern, "[BLOCKED]", RegexOptions.IgnoreCase);
        }

        return (cleaned, threats);
    }

    private static string ExtractMatch(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        return match.Success ? match.Value : "";
    }
}

public sealed class ThreatDetection
{
    public string Pattern { get; set; } = "";
    public string Description { get; set; } = "";
    public ThreatLevel Level { get; set; }
    public string Source { get; set; } = "";
    public DateTime DetectedAt { get; set; }
    public string MatchedText { get; set; } = "";
}

public enum ThreatLevel { Low, Medium, High, Critical }
