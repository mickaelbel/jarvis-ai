using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Voice;

/// <summary>
/// Wrapper de résilience autour de <see cref="ITextToSpeechService"/>.
/// Implémente : retry avec backoff, fallback vers TTS secondaire en cas d'échec,
/// journalisation des incidents.
/// </summary>
public sealed class ResilientTextToSpeechService : ITextToSpeechService, IAsyncDisposable
{
    private readonly ITextToSpeechService _primary;
    private readonly ITextToSpeechService? _fallback;
    private readonly ILogger<ResilientTextToSpeechService> _logger;
    private readonly int _maxRetries;

    public string Name => $"{_primary.Name} (resilient)";

    public IReadOnlyList<string> AvailableVoices => _primary.AvailableVoices;

    public ResilientTextToSpeechService(
        ITextToSpeechService primary,
        ITextToSpeechService? fallback,
        ILogger<ResilientTextToSpeechService> logger,
        int maxRetries = 2)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
        _maxRetries = maxRetries;
    }

    public async Task<byte[]> SynthesizeWavAsync(string text, string voice, float volume = 1.0f, float speed = 1.0f, CancellationToken cancellationToken = default)
    {
        Exception? lastEx = null;
        var attempt = 0;
        while (attempt <= _maxRetries)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var wav = await _primary.SynthesizeWavAsync(text, voice, volume, speed, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                if (wav is { Length: > 0 })
                {
                    if (attempt > 0)
                        _logger.LogInformation("[TTS-Resilient] Reprise réussie après {Attempt} tentative(s)", attempt);
                    return wav;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _logger.LogWarning(ex, "[TTS-Resilient] Échec synthèse (tentative {Attempt}/{Max})", attempt + 1, _maxRetries + 1);
            }

            attempt++;
            if (attempt > _maxRetries) break;
            await Task.Delay(TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
        }

        // Fallback sur TTS secondaire si dispo
        if (_fallback is not null && !ReferenceEquals(_fallback, _primary))
        {
            _logger.LogWarning("[TTS-Resilient] Bascule sur TTS de secours");
            try
            {
                return await _fallback.SynthesizeWavAsync(text, voice, volume, speed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "[TTS-Resilient] Échec du TTS de secours");
            }
        }

        return Array.Empty<byte>();
    }

    public ValueTask DisposeAsync()
    {
        if (_primary is IAsyncDisposable d1) return d1.DisposeAsync();
        return ValueTask.CompletedTask;
    }
}
