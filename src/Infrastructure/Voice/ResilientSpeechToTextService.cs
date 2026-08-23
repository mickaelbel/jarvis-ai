using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Voice;

/// <summary>
/// Wrapper de résilience autour de <see cref="ISpeechToTextService"/>.
/// Implémente : retry avec backoff exponentiel, ré-initialisation du moteur
/// sous-jacent en cas d'échec persistant, et journalisation des incidents.
/// Le but : le moteur vocal redémarre automatiquement après une erreur transitoire
/// (perte réseau, micro déconnecté, etc.) sans intervention utilisateur.
/// </summary>
public sealed class ResilientSpeechToTextService : ISpeechToTextService, IAsyncDisposable
{
    private readonly ISpeechToTextService _inner;
    private readonly Func<Task<bool>> _restartCallback;
    private readonly ILogger<ResilientSpeechToTextService> _logger;
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;

    public string Name => $"{_inner.Name} (resilient)";

    public ResilientSpeechToTextService(
        ISpeechToTextService inner,
        Func<Task<bool>> restartCallback,
        ILogger<ResilientSpeechToTextService> logger,
        int maxRetries = 3,
        TimeSpan? baseDelay = null)
    {
        _inner = inner;
        _restartCallback = restartCallback;
        _logger = logger;
        _maxRetries = maxRetries;
        _baseDelay = baseDelay ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => await _inner.IsAvailableAsync(cancellationToken).ConfigureAwait(false);

    public async Task<SttResult> TranscribeAsync(
        byte[] pcm16, int sampleRate, string? language = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exception? lastEx = null;
        var attempt = 0;
        while (attempt <= _maxRetries)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var result = await _inner.TranscribeAsync(pcm16, sampleRate, language, cancellationToken).ConfigureAwait(false);
                sw.Stop();

                if (result.Success)
                {
                    if (attempt > 0)
                        _logger.LogInformation("[STT-Resilient] Reprise réussie après {Attempt} tentative(s) en {Ms} ms", attempt, sw.ElapsedMilliseconds);
                    return result;
                }

                // Erreur retournée par l'implémentation interne
                if (attempt < _maxRetries)
                {
                    _logger.LogWarning("[STT-Resilient] Échec transcription (tentative {Attempt}/{Max}) : {Error}",
                        attempt + 1, _maxRetries + 1, result.Error ?? "(inconnu)");
                }
                lastEx = new InvalidOperationException(result.Error ?? "STT failed");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _logger.LogWarning(ex, "[STT-Resilient] Exception (tentative {Attempt}/{Max})", attempt + 1, _maxRetries + 1);
            }

            attempt++;
            if (attempt > _maxRetries) break;

            // Backoff exponentiel : 500 ms, 1 s, 2 s, ...
            var delay = TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { break; }

            // Relance le moteur sous-jacent après chaque échec persistant
            try
            {
                _logger.LogInformation("[STT-Resilient] Tentative de redémarrage du moteur sous-jacent");
                var ok = await _restartCallback().ConfigureAwait(false);
                if (!ok)
                    _logger.LogWarning("[STT-Resilient] Redémarrage échoué");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception restartEx)
            {
                _logger.LogWarning(restartEx, "[STT-Resilient] Erreur lors du redémarrage");
            }
        }

        return SttResult.Failed($"STT indisponible après {_maxRetries + 1} tentative(s) : {lastEx?.Message ?? "erreur inconnue"}");
    }

    public ValueTask DisposeAsync()
        => _inner is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
}
