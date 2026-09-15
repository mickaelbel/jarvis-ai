using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Voice;

/// <summary>
/// TTS via Microsoft Edge TTS (gratuit, voix neurales haute qualité).
/// Voix par défaut: fr-FR-HenriNeural (masculine, posée — style JARVIS).
/// Nécessite le serveur Python edge_tts_server.py (port EdgeTtsPort).
/// Failover : si le serveur principal est injoignable (échec réseau), bascule
/// automatiquement sur un serveur de secours (EdgeTtsPort2). L'état du serveur
/// actif est exposé via <see cref="ActiveBase"/> / <see cref="IsPrimaryActive"/>
/// et remonté dans /api/voice/engine/status (diagnostic utilisateur).
/// </summary>
public sealed class EdgeTtsTextToSpeechService : ITextToSpeechService, IAsyncDisposable
{
    private readonly HttpClient[] _servers;
    private readonly ILogger<EdgeTtsTextToSpeechService> _logger;
    private readonly string _defaultVoice;
    private int _activeServerIndex;
    private bool _serverAvailable = true;
    private DateTime _lastProbe = DateTime.MinValue;
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);
    private List<string>? _cachedVoices;

    public string Name => "EdgeTTS";

    // Honnêteté : ne renvoie que les voix réellement chargées depuis le serveur.
    // Une liste codée en dur affichait des voix alors que le moteur fallback SAPI
    // les remplaçait toutes par la même voix système (apparence « toutes identiques »).
    // Bonus : cache vidé dès qu'un serveur devient injoignable (voir IsAvailableAsync
    // et SynthesizeWavAsync) pour ne jamais proposer des voix « fantômes ».
    public IReadOnlyList<string> AvailableVoices => _cachedVoices ?? new List<string>();

    /// <summary>Base du serveur Edge TTS actuellement utilisé (après failover éventuel).</summary>
    public string ActiveBase => (_servers[_activeServerIndex].BaseAddress?.ToString() ?? string.Empty).TrimEnd('/');

    /// <summary>True si on utilise le serveur principal (pas de failover en cours).</summary>
    public bool IsPrimaryActive => _activeServerIndex == 0;

    public EdgeTtsTextToSpeechService(
        HttpClient httpClient,
        ILogger<EdgeTtsTextToSpeechService> logger,
        string? defaultVoice = null)
        : this(new[] { httpClient }, logger, defaultVoice)
    {
    }

    public EdgeTtsTextToSpeechService(
        IEnumerable<HttpClient> servers,
        ILogger<EdgeTtsTextToSpeechService> logger,
        string? defaultVoice = null)
    {
        _servers = servers?.Where(s => s is not null).ToArray() ?? Array.Empty<HttpClient>();
        if (_servers.Length == 0)
            throw new ArgumentException("Au moins un serveur Edge TTS est requis.", nameof(servers));
        _logger = logger;
        _defaultVoice = defaultVoice ?? "fr-FR-HenriNeural";
    }

    public async Task<byte[]> SynthesizeWavAsync(string text, string voice = "", float volume = 1.0f, float speed = 1.0f, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<byte>();

        if (!await IsAvailableAsync(cancellationToken))
            throw new InvalidOperationException($"Edge TTS server not available on port {VoicePaths.EdgeTtsPort}");

        var voiceName = string.IsNullOrWhiteSpace(voice) ? _defaultVoice : voice;

        // Mapper speed float → rate string: 1.0 = "+0%", 1.2 = "+20%", 0.8 = "-20%"
        var ratePercent = (int)((speed - 1.0f) * 100);
        var rate = ratePercent >= 0 ? $"+{ratePercent}%" : $"{ratePercent}%";

        var payload = new { text, voice = voiceName, rate, pitch = "+0Hz" };

        Exception? lastEx = null;
        var attempts = _servers.Length;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var index = (_activeServerIndex + attempt) % attempts;
            var server = _servers[index];
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(payload);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                using var response = await server.PostAsync("/synthesize", content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var audioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                _activeServerIndex = index;
                _logger.LogInformation(
                    "[EdgeTTS] Synthétisé ({Chars} chars, voice={Voice}, {Size} octets) sur {Server}",
                    text.Length, voiceName, audioBytes.Length, server.BaseAddress);

                return audioBytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                var willRetry = attempt < attempts - 1;
                if (willRetry)
                    _logger.LogWarning(ex, "[EdgeTTS] Échec sur {Server}, bascule sur le serveur suivant", server.BaseAddress);
            }
        }

        _logger.LogError(lastEx, "[EdgeTTS] Erreur de synthèse (tous les serveurs)");
        MarkServerDown();

        throw new InvalidOperationException(
            $"Synthèse Edge TTS impossible ({_servers.Length} serveur(s) injoignable(s)) : {lastEx?.Message}",
            lastEx);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_serverAvailable && DateTime.UtcNow - _lastProbe < ProbeInterval)
            return true;

        // Probes dans l'ordre principal → secours : on revient automatiquement au
        // serveur principal dès qu'il répond à nouveau.
        for (var i = 0; i < _servers.Length; i++)
        {
            try
            {
                var response = await _servers[i].GetAsync("/health", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    _activeServerIndex = i;
                    _serverAvailable = true;
                    _lastProbe = DateTime.UtcNow;

                    if (_cachedVoices is null)
                        await LoadVoicesAsync(cancellationToken);

                    return true;
                }
            }
            catch
            {
                // Serveur injoignable : on essaie le suivant.
            }
        }

        MarkServerDown();
        return false;
    }

    private void MarkServerDown()
    {
        _serverAvailable = false;
        _lastProbe = DateTime.UtcNow;
        // Bug « toutes les mêmes voix » : on purge le cache des voix dès qu'aucun
        // serveur ne répond, pour que l'UI ne propose plus des voix « fantômes »
        // qui seraient remplacées silencieusement par la même voix SAPI.
        _cachedVoices = null;
    }

    private async Task LoadVoicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _servers[_activeServerIndex].GetAsync("/voices", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("voices", out var voicesArr))
                {
                    _cachedVoices = voicesArr.EnumerateArray()
                        .Select(v => v.GetProperty("ShortName").GetString() ?? "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .ToList();
                }
            }
        }
        catch
        {
            // Serveur injoignable pendant le chargement : le cache reste vide,
            // l'UI n'affichera aucune voix « fantôme ».
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}