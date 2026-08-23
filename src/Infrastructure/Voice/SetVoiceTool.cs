using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Voice;
using JarvisAI.Domain.Security;

namespace JarvisAI.Infrastructure.Voice;

/// <summary>
/// Permet à l'IA de changer la voix de synthèse vocale sur demande en langage
/// naturel ("parle avec une voix féminine", "utilise une voix masculine", ...).
/// Le genre demandé est mappé vers une voix Piper disponible (en priorité dans la
/// langue courante de TTS), ou une voix précise peut être passée explicitement.
/// </summary>
public sealed class SetVoiceTool : ITool
{
    private readonly IVoiceSettingsStore _settings;
    private readonly ITextToSpeechService _tts;

    public string Name => "set_voice";

    public string Description =>
        "Change la voix de synthèse vocale de Jarvis. Paramètres : gender (female/male ou " +
        "féminin/masculin) pour choisir automatiquement une voix du bon genre, voice (nom exact " +
        "d'une voix disponible) pour une voix précise, language (ex: fr, en) pour préférer une " +
        "langue. Un seul paramètre suffit.";

    public string Category => "voice";

    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("gender", "genre de la voix : female/male (ou féminin/masculin)", typeof(string)),
        new ToolParameter("voice", "nom exact d'une voix disponible", typeof(string)),
        new ToolParameter("language", "préférence de langue de la voix (ex: fr, en)", typeof(string)),
    };

    private static readonly string[] FemaleVoiceMarkers =
    {
        "amy", "ella", "anna", "siwis", "maria", "lisa", "samantha", "serena",
        "isabelle", "catherine", "lucy", "jenny", "susan", "alice", "jane",
        "karen", "nancy", "zira", "aria", "mia", "sarah", "emma", "olivia",
        "hazel", "heather", "kathleen", "steph", "erin", "vicki", "marcia",
        "michelle", "salli", "joanna", "kendra", "kimberly", "ivy", "penelope",
        "camilla", "melina", "layla", "kajal", "sixtine", "margaux",
    };

    private static readonly string[] MaleVoiceMarkers =
    {
        "upmc", "tom", "joe", "ryan", "david", "will", "peter", "daniel",
        "colin", "sam", "guy", "dennis", "arthur", "george", "oliver",
        "edward", "harry", "charles", "james", "kyle", "matthew", "justin",
        "joey", "stephen", "ricardo", "miguel", "fernando", "antonio", "carlos",
        "francesco", "fabio", "theo", "quentin", "alexis",
    };

    public SetVoiceTool(IVoiceSettingsStore settings, ITextToSpeechService tts)
    {
        _settings = settings;
        _tts = tts;
    }

    public Task<ToolResult> ExecuteAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        var current = _settings.Get();
        parameters.TryGetValue("gender", out var gender);
        parameters.TryGetValue("voice", out var voice);
        parameters.TryGetValue("language", out var language);

        var available = _tts.AvailableVoices.ToList();
        var chosen = PickVoice(available, current, voice, gender, language);

        if (chosen is null)
        {
            return Task.FromResult(ToolResult.Failed(
                "Aucune voix Piper disponible. La voix n'a pas pu être changée."));
        }

        current.TtsVoice = chosen;
        _settings.Save(current);

        var genderLabel = ClassifyGender(chosen) == Gender.Male ? "masculin" : "féminin";
        return Task.FromResult(ToolResult.Succeeded(
            $"Voix réglée : « {chosen} » (genre : {genderLabel}). Cette voix sera utilisée pour la prochaine réponse vocale."));
    }

    private enum Gender { Female, Male, Unknown }

    private static Gender ClassifyGender(string voice)
    {
        var lower = voice.ToLowerInvariant();
        if (FemaleVoiceMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
            return Gender.Female;
        if (MaleVoiceMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
            return Gender.Male;
        return Gender.Unknown;
    }

    private static Gender NormalizeGender(string? gender)
    {
        if (string.IsNullOrWhiteSpace(gender)) return Gender.Unknown;
        var g = gender.Trim().ToLowerInvariant();
        if (g is "female" or "femme" or "féminin" or "feminin" or "voix féminine" or "fem")
            return Gender.Female;
        if (g is "male" or "homme" or "masculin" or "voix masculine")
            return Gender.Male;
        return Gender.Unknown;
    }

    private static string? PickVoice(
        List<string> available,
        VoiceSettings current,
        string? explicitVoice,
        string? gender,
        string? language)
    {
        if (available.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(explicitVoice))
        {
            var exact = available.FirstOrDefault(v =>
                string.Equals(v, explicitVoice, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        IEnumerable<string> pool = available;

        if (!string.IsNullOrWhiteSpace(language))
        {
            var langPool = available.Where(v =>
                v.StartsWith(language + "_", StringComparison.OrdinalIgnoreCase)
                || v.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)).ToList();
            if (langPool.Count > 0)
                pool = langPool;
        }
        else if (!string.IsNullOrWhiteSpace(current.TtsLanguage))
        {
            var langPool = available.Where(v =>
                v.StartsWith(current.TtsLanguage + "_", StringComparison.OrdinalIgnoreCase)
                || v.StartsWith(current.TtsLanguage + "-", StringComparison.OrdinalIgnoreCase)).ToList();
            if (langPool.Count > 0)
                pool = langPool;
        }

        var target = NormalizeGender(gender);
        if (target == Gender.Male)
        {
            var match = pool.FirstOrDefault(v => ClassifyGender(v) == Gender.Male);
            if (match is not null) return match;
        }
        else if (target == Gender.Female)
        {
            var match = pool.FirstOrDefault(v => ClassifyGender(v) == Gender.Female);
            if (match is not null) return match;
        }

        return pool.FirstOrDefault();
    }
}
