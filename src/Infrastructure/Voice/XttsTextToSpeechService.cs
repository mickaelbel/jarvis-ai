using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Voice;

/// <summary>
/// Synthèse par XTTS-v2 local (scripts/tts_xtts_server.py, port 17003) —
/// voix clonée à partir d'un extrait de référence. Si le serveur ne répond
/// pas, l'exception remonte au ResilientTextToSpeechService qui retombe sur Piper.
/// </summary>
public sealed class XttsTextToSpeechService : Application.Voice.ITextToSpeechService
{
    public string Name => "xtts";

    private readonly HttpClient _http;
    private readonly ILogger<XttsTextToSpeechService> _logger;
    private IReadOnlyList<string>? _voixCachees;
    private bool _serveurAbsent;

    public XttsTextToSpeechService(HttpClient http, ILogger<XttsTextToSpeechService> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Le serveur tourne-t-il ? (cache négatif 5 min pour éviter les délais)</summary>
    private bool ServeurDisponible()
    {
        if (_serveurAbsent && DateTime.UtcNow - _dernierEchec < TimeSpan.FromMinutes(5)) return false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var json = _http.GetStringAsync("/health", cts.Token).GetAwaiter().GetResult();
            var ok = json.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase);
            if (ok) { _serveurAbsent = false; }
            return ok;
        }
        catch
        {
            _serveurAbsent = true;
            _dernierEchec = DateTime.UtcNow;
            return false;
        }
    }

    private DateTime _dernierEchec = DateTime.MinValue;

    public IReadOnlyList<string> AvailableVoices
    {
        get
        {
            if (_voixCachees is not null) return _voixCachees;
            if (!ServeurDisponible()) return Array.Empty<string>();
            try
            {
                var json = _http.GetStringAsync("/health").GetAwaiter().GetResult();
                var clonee = json.Contains("\"speaker\":true", StringComparison.OrdinalIgnoreCase);
                _voixCachees = new[] { clonee ? "xtts-voix-clonee" : "xtts-native" };
            }
            catch { _voixCachees = Array.Empty<string>(); }
            return _voixCachees;
        }
    }

    public async Task<byte[]> SynthesizeWavAsync(string text, string voice, float volume = 1.0f, float speed = 1.0f, CancellationToken cancellationToken = default)
    {
        if (!ServeurDisponible())
            throw new InvalidOperationException("Serveur XTTS indisponible.");

        using var contenu = new StringContent(
            JsonSerializer.Serialize(new { text, language = "fr" }),
            Encoding.UTF8, "application/json");

        using var reponse = await _http.PostAsync("/synthesize", contenu, cancellationToken);
        reponse.EnsureSuccessStatusCode();
        return await reponse.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>Clonage : envoie un WAV de référence (6-30 s) au serveur.</summary>
    public async Task<bool> ClonerVoixAsync(string cheminWav, CancellationToken ct = default)
    {
        if (!File.Exists(cheminWav))
            throw new FileNotFoundException("Fichier de référence introuvable.", cheminWav);

        using var flux = File.OpenRead(cheminWav);
        using var form = new MultipartFormDataContent();
        form.Add(new StreamContent(flux), "file", Path.GetFileName(cheminWav));
        using var reponse = await _http.PostAsync("/speaker", form, ct);
        if (reponse.IsSuccessStatusCode)
        {
            _voixCachees = null; // re-probe au prochain accès
            _logger.LogInformation("[XTTS] voix clonée depuis {Chemin}", cheminWav);
        }
        return reponse.IsSuccessStatusCode;
    }
}
