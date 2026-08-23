using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

/// <summary>
/// Détecteur de mot-clé local via le serveur Python OpenWakeWord
/// (wakeword_server.py). Démarre le serveur au besoin (comme faster-whisper),
/// lui envoie les morceaux PCM16 mono et renvoie la détection.
/// </summary>
public sealed class OpenWakeWordService : IWakeWordDetector, IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenWakeWordService> _logger;
    private readonly string? _venvPython;
    private readonly string? _serverScript;
    private readonly object _startLock = new();
    private bool _startAttempted;

    public string Name => "openwakeword (Python)";

    public OpenWakeWordService(
        HttpClient httpClient,
        ILogger<OpenWakeWordService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _venvPython = VoicePaths.FindPythonVenv();
        _serverScript = VoicePaths.FindWakeWordServerScript();
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var response = await _httpClient.GetAsync("/health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<WakeWordDetection> DetectAsync(
        byte[] pcm16, int sampleRate, CancellationToken cancellationToken = default)
    {
        if (!await EnsureStartedAsync(cancellationToken))
        {
            return new WakeWordDetection(false, 0, 0, 0);
        }

        try
        {
            using var content = new ByteArrayContent(pcm16);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Headers.Add("X-Sample-Rate", sampleRate.ToString());

            var response = await _httpClient.PostAsync("/detect", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("[WakeWord] HTTP {Status}", response.StatusCode);
                return new WakeWordDetection(false, 0, 0, 0);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _))
            {
                return new WakeWordDetection(false, 0, 0, 0);
            }

            var triggered = root.TryGetProperty("triggered", out var t) && t.ValueKind == JsonValueKind.True;
            var score = root.TryGetProperty("score", out var s) ? s.GetDouble() : 0;
            var clicks = root.TryGetProperty("clicks", out var c) ? c.GetDouble() : 0;
            var elapsed = root.TryGetProperty("elapsed_ms", out var e) ? e.GetInt32() : 0;
            return new WakeWordDetection(triggered, score, clicks, elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[WakeWord] Detection request failed");
            return new WakeWordDetection(false, 0, 0, 0);
        }
    }

    public async Task<bool> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (await IsAvailableAsync(cancellationToken)) return true;

        lock (_startLock)
        {
            if (_startAttempted) return false;
            _startAttempted = true;
        }

        if (string.IsNullOrEmpty(_venvPython) || string.IsNullOrEmpty(_serverScript))
        {
            _logger.LogError("[WakeWord] Python venv or wakeword_server.py not found (venv={Venv}, script={Script})", _venvPython, _serverScript);
            return false;
        }

        _logger.LogInformation("[WakeWord] Starting wake-word server: {Python} {Script}", _venvPython, _serverScript);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _venvPython,
                Arguments = $"\"{_serverScript}\"",
                WorkingDirectory = Path.GetDirectoryName(_serverScript),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WakeWord] Failed to start server");
            return false;
        }

        for (var i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsAvailableAsync(cancellationToken)) return true;
            await Task.Delay(1000, cancellationToken);
        }

        _logger.LogWarning("[WakeWord] Server did not become ready within 30s");
        return false;
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
