using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Voice;

/// <summary>
/// Moteur TTS « auto » réel : au lieu d'un mapping figé (edge → piper → windows),
/// il relit la configuration à chaque appel et choisit, dans l'ordre :
/// 1. le moteur explicitement configuré (TtsEngine ≠ auto),
/// 2. le moteur dont la liste de voix réelle contient la voix demandée,
/// 3. le premier moteur « disponible » (liste de voix non vide),
/// 4. Windows/SAPI en toute dernière extrémité.
/// Si la synthèse échoue sur le moteur choisi, on retombe sur le moteur suivant
/// disponible — comme un wrapper résilient, mais sans mapping codé en dur.
/// </summary>
public sealed class AutoTtsEngineService : ITextToSpeechService
{
    private readonly ITextToSpeechService[] _engines;
    private readonly IVoiceSettingsStore _settings;
    private readonly ILogger<AutoTtsEngineService> _logger;

    public string Name => "auto";

    public IReadOnlyList<string> AvailableVoices
    {
        get
        {
            var voices = new List<string>();
            foreach (var engine in _engines)
                voices.AddRange(engine.AvailableVoices);
            return voices;
        }
    }

    public AutoTtsEngineService(
        IEnumerable<ITextToSpeechService> engines,
        IVoiceSettingsStore settings,
        ILogger<AutoTtsEngineService> logger)
    {
        _engines = engines.Where(e => e.Name is { Length: > 0 }).ToArray();
        _settings = settings;
        _logger = logger;
    }

    public async Task<byte[]> SynthesizeWavAsync(string text, string voice, float volume = 1.0f, float speed = 1.0f, CancellationToken cancellationToken = default)
    {
        text = TtsPronunciation.Normalize(text, voice);

        var candidates = BuildCandidateChain(voice);

        Exception? lastEx = null;
        foreach (var engine in candidates)
        {
            try
            {
                var wav = await engine.SynthesizeWavAsync(text, voice, volume, speed, cancellationToken).ConfigureAwait(false);
                if (wav is { Length: > 0 })
                    return wav;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                if (candidates.SkipWhile(c => !ReferenceEquals(c, engine)).Skip(1).Any())
                    _logger.LogWarning(ex, "[TTS-auto] Échec sur {Engine}, bascule sur le suivant", engine.Name);
                else
                    _logger.LogWarning(ex, "[TTS-auto] Échec sur {Engine} (dernier candidat)", engine.Name);
            }
        }

        if (lastEx is not null)
            _logger.LogError(lastEx, "[TTS-auto] Aucun moteur n'a pu synthétiser");

        return Array.Empty<byte>();
    }

    private IReadOnlyList<ITextToSpeechService> BuildCandidateChain(string voice)
    {
        var configured = _settings.Get().TtsEngine?.Trim() ?? string.Empty;
        var list = new List<ITextToSpeechService>();

        // 1. Moteur explicitement configuré, s'il existe.
        if (!string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(configured))
        {
var explicitEngine = _engines.FirstOrDefault(e =>
            e.Name.Contains(configured, StringComparison.OrdinalIgnoreCase) ||
            configured.Contains(e.Name, StringComparison.OrdinalIgnoreCase));
            if (explicitEngine is not null && !list.Contains(explicitEngine))
                list.Add(explicitEngine);
        }

        // 2. Le moteur dont la liste de voix réelle contient la voix demandée.
        if (!string.IsNullOrWhiteSpace(voice))
        {
            foreach (var engine in _engines)
            {
                if (engine.AvailableVoices.Contains(voice) && !list.Contains(engine))
                    list.Add(engine);
            }
        }

        // 3. Les moteurs réellement disponibles (liste non vide), au sens d'Edge TTS.
        foreach (var engine in _engines)
        {
            if (engine.AvailableVoices.Count > 0 && !list.Contains(engine))
                list.Add(engine);
        }

        // 4. Windows/SAPI en secours universel (disponible hors ligne).
        foreach (var engine in _engines)
        {
            if (!list.Contains(engine))
                list.Add(engine);
        }

        return list;
    }
}