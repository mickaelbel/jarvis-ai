using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.AI;

public interface IIntentRecognizer
{
    IntentResult Recognize(string input);
}

public sealed class IntentRecognizer : IIntentRecognizer
{
    private readonly ILogger<IntentRecognizer> _logger;

    private static readonly Dictionary<string, string[]> IntentPatterns = new()
    {
        ["file_operation"] = new[] { "créer fichier", "modifier fichier", "supprimer fichier", "lire fichier", "copier fichier", "déplacer fichier", "rename", "cherche fichier", "liste dossier", "crée un fichier", "écris dans", "sauvegarde" },
        ["terminal_command"] = new[] { "exécute", "lance commande", "terminal", "powershell", "cmd", "git ", "npm ", "dotnet ", "pip ", "compile", "build" },
        ["web_search"] = new[] { "cherche sur", "google", "recherche web", "site web", "url ", "ouvre http", "navigue vers" },
        ["app_launch"] = new[] { "ouvre ", "lance ", "démarrer ", "start ", "execute application" },
        ["system_info"] = new[] { "info système", "cpu", "mémoire", "ram", "disque", "réseau", "battery", "processus" },
        ["memory"] = new[] { "souviens-toi", "retiens", "mémoire", "rappelle-toi", "forget", "oublie" },
        ["voice"] = new[] { "dis ", "parle", "prononce", "audio", "tts", "voice" },
        ["screenshot"] = new[] { "capture", "screenshot", "écran", "photo écran", "capture d'écran" },
        ["email"] = new[] { "mail", "email", "courriel", "envoie un mail", "vérifie messagerie" },
        ["calendar"] = new[] { "calendrier", "rendez-vous", "meeting", "agenda", "événement" },
        ["weather"] = new[] { "météo", "weather", "température", "prévisions" },
        ["reminder"] = new[] { "rappelle-moi", "reminder", "alarme", "minuteur", "timer" },
    };

    public IntentRecognizer(ILogger<IntentRecognizer> logger)
    {
        _logger = logger;
    }

    public IntentResult Recognize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new IntentResult { Intent = "unknown", Confidence = 0 };

        var lower = input.ToLowerInvariant().Trim();
        var scores = new Dictionary<string, double>();

        foreach (var kv in IntentPatterns)
        {
            var matchCount = kv.Value.Count(pattern => lower.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (matchCount > 0)
            {
                scores[kv.Key] = (double)matchCount / kv.Value.Length;
            }
        }

        if (scores.Count == 0)
            return new IntentResult { Intent = "general", Confidence = 0.3, AllScores = scores };

        var best = scores.OrderByDescending(kv => kv.Value).First();
        var confidence = Math.Min(0.95, best.Value + 0.3);

        _logger.LogDebug("[Intent] '{Input}' → {Intent} (confidence: {Conf:P0})",
            input[..Math.Min(50, input.Length)], best.Key, confidence);

        return new IntentResult
        {
            Intent = best.Key,
            Confidence = confidence,
            RequiresConfirmation = confidence < 0.5,
            AllScores = scores
        };
    }
}

public sealed class IntentResult
{
    public string Intent { get; set; } = "";
    public double Confidence { get; set; }
    public bool RequiresConfirmation { get; set; }
    public Dictionary<string, double> AllScores { get; set; } = new();
}
