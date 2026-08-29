using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Voice;

/// <summary>
/// TTS via Microsoft Edge TTS (gratuit, voix neurales haute qualité).
/// Voix par défaut: fr-FR-HenriNeural (masculine, posée — style JARVIS).
/// Nécessite le serveur Python edge_tts_server.py sur le port 17004.
/// </summary>
public sealed class EdgeTtsTextToSpeechService : ITextToSpeechService, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<EdgeTtsTextToSpeechService> _logger;
    private readonly string _defaultVoice;
    private bool _serverAvailable = true;
    private DateTime _lastProbe = DateTime.MinValue;
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);
    private List<string>? _cachedVoices;

    public string Name => "EdgeTTS";

    public IReadOnlyList<string> AvailableVoices => _cachedVoices ?? new List<string>
    {
        "fr-FR-HenriNeural",
        "fr-FR-DeniseNeural",
        "fr-FR-EloiseNeural",
        "fr-FR-JacquesNeural",
        "fr-FR-YvetteNeural"
    };

    public EdgeTtsTextToSpeechService(HttpClient httpClient, ILogger<EdgeTtsTextToSpeechService> logger, string? defaultVoice = null)
    {
        _http = httpClient;
        _logger = logger;
        _defaultVoice = defaultVoice ?? "fr-FR-HenriNeural";
    }

    public async Task<byte[]> SynthesizeWavAsync(string text, string voice = "", float volume = 1.0f, float speed = 1.0f, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<byte>();

        if (!await IsAvailableAsync(cancellationToken))
            throw new InvalidOperationException("Edge TTS server not available on port 17004");

        var voiceName = string.IsNullOrWhiteSpace(voice) ? _defaultVoice : voice;

        try
        {
            // Mapper speed float → rate string: 1.0 = "+0%", 1.2 = "+20%", 0.8 = "-20%"
            var ratePercent = (int)((speed - 1.0f) * 100);
            var rate = ratePercent >= 0 ? $"+{ratePercent}%" : $"{ratePercent}%";

            var request = new { text, voice = voiceName, rate, pitch = "+0Hz" };
            var json = System.Text.Json.JsonSerializer.Serialize(request);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            using var response = await _http.PostAsync("/synthesize", content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var audioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            _logger.LogInformation("[EdgeTTS] Synthétisé ({Chars} chars, voice={Voice}, {Size} octets)",
                text.Length, voiceName, audioBytes.Length);

            return audioBytes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EdgeTTS] Erreur de synthèse");
            _serverAvailable = false;
            _lastProbe = DateTime.UtcNow;
            throw;
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_serverAvailable && DateTime.UtcNow - _lastProbe < ProbeInterval)
            return true;

        try
        {
            var response = await _http.GetAsync("/health", cancellationToken);
            _serverAvailable = response.IsSuccessStatusCode;
            _lastProbe = DateTime.UtcNow;

            if (_serverAvailable && _cachedVoices is null)
                await LoadVoicesAsync(cancellationToken);

            return _serverAvailable;
        }
        catch
        {
            _serverAvailable = false;
            _lastProbe = DateTime.UtcNow;
            return false;
        }
    }

    private async Task LoadVoicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _http.GetAsync("/voices", cancellationToken);
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
        catch { /* fallback to hardcoded list */ }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
